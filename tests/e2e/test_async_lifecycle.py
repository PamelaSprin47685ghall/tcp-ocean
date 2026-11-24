#!/usr/bin/env python3
"""Run against a prebuilt CLI: python3 test_async_lifecycle.py /path/to/tbtun."""

import os
import re
import subprocess
import sys
import time
from contextlib import ExitStack
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from socket import socket
from tempfile import TemporaryDirectory
from threading import Thread
from urllib.request import ProxyHandler, build_opener


def free_port():
    with socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def start(stack, command, log):
    with log.open("wb") as output:
        process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                   env={k: v for k, v in os.environ.items() if k != "TBTUN_ASYNC_MODE"})

    def stop():
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=4)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()

    stack.callback(stop)
    return process


def wait_for(process, log, line, count=1, peer=None):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if process.poll() is not None or (peer and peer[0].poll() is not None):
            break
        if log.read_text().count(line) >= count:
            return
        time.sleep(0.05)
    message = f"Expected {line!r} in {log.name}:\n{log.read_text()}"
    if peer:
        message += f"\n{peer[1].name}:\n{peer[1].read_text()}"
    raise AssertionError(message)


def eventfd_count(process):
    count = 0
    for descriptor in Path(f"/proc/{process.pid}/fd").iterdir():
        try:
            count += os.readlink(descriptor) == "anon_inode:[eventfd]"
        except FileNotFoundError:
            # A socket can close while we list the server's descriptors.
            pass
    return count


def check_idle_server(binary, key, target, work):
    endpoint = f"127.0.0.1:{free_port()}"
    log = work / "idle-server.log"
    with ExitStack() as stack:
        server = start(stack, [binary, "server", "--listen", endpoint, "--target", target,
                               "--psk-file", key, "--stats-s", "1"], log)
        wait_for(server, log, "stats sessions=0", count=3)
        rounds = [int(n) for n in re.findall(r"stats sessions=0[^\n]*\biter_n=(\d+)", log.read_text())]
        # Ignore startup. Nothing but the one-second stats timer should wake the listener.
        if len(rounds) < 3 or max(rounds[1:]) > 10:
            raise AssertionError(f"Idle server ran {rounds} main-loop rounds per second")
    print(f"Idle server main-loop rounds per second: {rounds[1:]}")


def check(binary):
    with TemporaryDirectory(prefix="tbt-async-lifecycle-") as tmp, ExitStack() as stack:
        work = Path(tmp)
        payload = bytes(range(256)) * 4096
        (work / "data").write_bytes(payload)
        key = work / "psk"
        key.write_text("ab" * 32 + "\n")  # Test key; both listeners are on loopback.

        def handler(*args):
            return SimpleHTTPRequestHandler(*args, directory=work)

        origin = stack.enter_context(ThreadingHTTPServer(("127.0.0.1", 0), handler))
        thread = Thread(target=origin.serve_forever, daemon=True)
        thread.start()
        stack.callback(thread.join)
        stack.callback(origin.shutdown)

        target = f"127.0.0.1:{origin.server_port}"
        check_idle_server(binary, key, target, work)
        port = free_port()
        endpoint = f"127.0.0.1:{port}"
        server_log = work / "server.log"
        server = start(stack, [binary, "server", "--listen", endpoint, "--max-paths", "256",
                               "--target", target, "--psk-file", key], server_log)
        wait_for(server, server_log, "readiness wait:")

        # No --stats-s: a periodic log must not wake the stalled JOIN or close.
        opener = build_opener(ProxyHandler({}))
        idle_eventfds = None
        for index in range(1, 5):
            client_port = free_port()
            client_log = work / f"client-{index}.log"
            with ExitStack() as session:
                client = start(session, [binary, "client", "--server", endpoint, "--paths", "256",
                                         "--listen", f"127.0.0.1:{client_port}", "--psk-file", key], client_log)
                wait_for(client, client_log, "paths ready: paths=256\n", peer=(server, server_log))
                with opener.open(f"http://127.0.0.1:{client_port}/data", timeout=5) as response:
                    if response.read() != payload:
                        raise AssertionError("The received data differs from the file")
                client.terminate()
                if client.wait(timeout=4) != 0:
                    raise AssertionError(client_log.read_text())
                wait_for(server, server_log, "closed by peer", index)
                if sys.platform == "linux":
                    count = eventfd_count(server)
                    if idle_eventfds is None:
                        idle_eventfds = count  # The shared IO pool starts on the first session.
                    elif count != idle_eventfds:
                        raise AssertionError(f"Idle server eventfd count changed: {idle_eventfds} -> {count}")
            print(f"Session {index}: 256 paths, 1 MiB received, then closed")


if __name__ == "__main__":
    check(str(Path(sys.argv[1]).resolve(strict=True)))
