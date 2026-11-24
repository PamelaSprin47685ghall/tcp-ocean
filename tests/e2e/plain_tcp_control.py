#!/usr/bin/env python3
"""Transfer one file over ordinary TCP. Run both sides with the same token."""

import argparse
import asyncio
import hashlib
import hmac
import json
from pathlib import Path
import socket
import struct
import time


COUNTER_OFFSETS = {"bytes_acked": 120, "bytes_sent": 200, "bytes_retrans": 208}


def snapshot(writers):
    counters = {}
    for index, writer in writers.items():
        sock = writer.get_extra_info("socket")
        try:
            info = sock.getsockopt(socket.IPPROTO_TCP, socket.TCP_INFO, 216)
        except OSError:
            continue
        # Linux UAPI tcp_info. Older kernels return a shorter record.
        if len(info) == 216:
            counters[index] = {name: struct.unpack_from("=Q", info, offset)[0]
                               for name, offset in COUNTER_OFFSETS.items()}
    return counters


async def expect(reader, value):
    if await reader.readexactly(len(value)) != value:
        raise ValueError("Unexpected control message")


def span(total, paths, index):
    return index * total // paths, (index + 1) * total // paths


async def close_all(writers):
    for writer in writers:
        writer.close()
    await asyncio.gather(*(writer.wait_closed() for writer in writers), return_exceptions=True)


async def send(args):
    payload = memoryview(args.file.read_bytes())
    if len(payload) < args.paths:
        raise ValueError("The file must contain at least one byte per path")
    writers = {}
    handlers = {}
    registered = asyncio.Event()
    armed = asyncio.Event()
    start = asyncio.Event()
    done = asyncio.Event()
    sampled = asyncio.Event()
    arm_count = 0
    confirmed = {}

    async def accept(reader, writer):
        nonlocal arm_count
        try:
            if writer.get_extra_info("peername")[0] != args.peer:
                return
            header = await reader.readexactly(len(args.token) + 2)
            index = int.from_bytes(header[-2:])
            if not hmac.compare_digest(header[:-2], args.token) or index >= args.paths or index in writers:
                return
            writers[index] = writer
            handlers[index] = asyncio.current_task()
            writer.write(b"R" + len(payload).to_bytes(8))
            await writer.drain()
            if len(writers) == args.paths:
                registered.set()
            await expect(reader, b"G")
            arm_count += 1
            if arm_count == args.paths:
                armed.set()
            await start.wait()
            lo, hi = span(len(payload), args.paths, index)
            part = payload[lo:hi]
            writer.write(part)
            await writer.drain()
            await expect(reader, hashlib.sha256(part).digest())
            confirmed[index] = len(part)
            if len(confirmed) == args.paths:
                done.set()
            await sampled.wait()
            writer.write(b"S")
            await writer.drain()
        except (ConnectionError, asyncio.IncompleteReadError, ValueError):
            return
        finally:
            writer.close()

    server = await asyncio.start_server(accept, args.host, args.port, backlog=max(128, args.paths))
    async with server:
        print(json.dumps({"listening": args.port, "bytes": len(payload), "paths": args.paths}), flush=True)
        try:
            try:
                async with asyncio.timeout(args.setup):
                    await registered.wait()
                    await armed.wait()
            except TimeoutError:
                print(json.dumps({"registered_paths": len(writers), "armed_paths": arm_count,
                                  "error": "Registration deadline"}), flush=True)
                return False
            before = snapshot(writers)
            started = time.monotonic()
            start.set()
            try:
                await asyncio.wait_for(done.wait(), args.seconds)
            except TimeoutError:
                pass
            elapsed = time.monotonic() - started
            after = snapshot(writers)
            paired = before.keys() & after.keys()
            result = {"expected_bytes": len(payload), "verified_bytes": sum(confirmed.values()),
                      "verified_paths": len(confirmed), "matched_paths": len(paired), "seconds": elapsed,
                      "sha256": hashlib.sha256(payload).hexdigest()}
            for field in COUNTER_OFFSETS:
                result[field] = sum(after[i][field] - before[i][field] for i in paired) if paired else None
            print(json.dumps(result), flush=True)
            sampled.set()
            complete = len(confirmed) == len(paired) == args.paths
            if complete:
                await asyncio.wait_for(asyncio.gather(*handlers.values()), 2)
            return complete
        finally:
            await close_all(writers.values())


async def receive(args):
    writers = {}
    ready = asyncio.Event()
    start = asyncio.Event()
    sem = asyncio.Semaphore(args.parallel_open)
    reads = {}
    sizes = {}
    finish = {}

    async def transfer(index):
        async with sem:
            reader, writer = await asyncio.open_connection(args.host, args.port)
            writers[index] = writer
            writer.write(args.token + index.to_bytes(2))
            await writer.drain()
            await expect(reader, b"R")
            sizes[index] = int.from_bytes(await reader.readexactly(8))
            if len(sizes) == args.paths:
                ready.set()
        await start.wait()
        writer.write(b"G")
        await writer.drain()
        lo, hi = span(sizes[index], args.paths, index)
        data = reads[index] = bytearray()
        while len(data) < hi - lo:
            block = await reader.read(hi - lo - len(data))
            if not block:
                raise EOFError(f"Path {index} closed after {len(data)} bytes")
            data.extend(block)
        finish[index] = time.monotonic()
        writer.write(hashlib.sha256(data).digest())
        await writer.drain()
        await expect(reader, b"S")

    tasks = [asyncio.create_task(transfer(i)) for i in range(args.paths)]
    started = None
    failure = None
    try:
        await asyncio.wait_for(ready.wait(), args.setup)
        if len(set(sizes.values())) != 1:
            raise ValueError("Paths reported different file lengths")
        started = time.monotonic()
        start.set()
        outcomes = await asyncio.wait_for(asyncio.gather(*tasks, return_exceptions=True), args.seconds + 5)
        failure = next((repr(outcome) for outcome in outcomes if isinstance(outcome, Exception)), None)
    except (TimeoutError, ConnectionError, EOFError, asyncio.IncompleteReadError, ValueError) as error:
        failure = str(error) or type(error).__name__
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await close_all(writers.values())

    digest = None
    if len(finish) == args.paths:
        sha = hashlib.sha256()
        for i in range(args.paths):
            sha.update(reads[i])
        digest = sha.hexdigest()
    print(json.dumps({"bytes": sum(map(len, reads.values())), "registered_paths": len(sizes),
                      "completed_paths": len(finish), "sha256": digest, "error": failure,
                      "seconds": (max(finish.values()) if digest else time.monotonic()) - started
                      if started is not None else None}), flush=True)
    return digest is not None and failure is None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("side", choices=("send", "receive"))
    parser.add_argument("host", help="IPv4 listen address or sender address")
    parser.add_argument("port", type=int)
    parser.add_argument("--token-file", type=Path, required=True, help="file containing 32 hex digits")
    parser.add_argument("--peer", help="only accept this IPv4 source address")
    parser.add_argument("--file", type=Path, help="file the sender transmits")
    parser.add_argument("--paths", type=int, default=256)
    parser.add_argument("--parallel-open", type=int, default=32, help="maximum concurrent TCP handshakes")
    parser.add_argument("--setup", type=int, default=30, help="registration deadline in seconds")
    parser.add_argument("--seconds", type=int, default=20, help="transfer deadline in seconds")
    args = parser.parse_args()
    args.token = bytes.fromhex(args.token_file.read_text().strip())
    if len(args.token) != 16 or not 1 <= args.paths <= 256 or not 1 <= args.parallel_open <= 256:
        parser.error("Expected a 16-byte token, 1–256 paths, and 1–256 concurrent handshakes")
    if min(args.setup, args.seconds) < 1 or args.setup + args.seconds + 5 > 60:
        parser.error("Positive registration and transfer deadlines plus 5 seconds must fit in 60 seconds")
    if args.side == "send" and (not args.peer or not args.file):
        parser.error("The sender requires --file and --peer")
    return 0 if asyncio.run(send(args) if args.side == "send" else receive(args)) else 1


if __name__ == "__main__":
    raise SystemExit(main())
