#!/usr/bin/env python3
"""Post-run stamping: host-pause evidence and wanem/server conservation.
Invoked by run.sh as: python3 stamp_end.py WORK_DIR RUN_STARTED_UNIX HOST_PAUSE_DISCARD
Prints one-line summaries; writes host-pause.txt, conservation.txt, and run-invalid
when a pre-declared host-pause discard applies. Event-loop stall is never a reason.
"""
import datetime
import re
import sys
from pathlib import Path


def parse_dmesg_ts(s):
    s = s.strip()
    for fmt in ("%a %b %d %H:%M:%S %Y",):
        try:
            return int(datetime.datetime.strptime(s, fmt).timestamp())
        except ValueError:
            pass
    return None


def last_max_frames(path: Path):
    mx = None
    if not path.exists():
        return None
    for line in path.read_text(errors="replace").splitlines():
        # Session path stats; ignore post-close "sessions=0 frames_in=0".
        if "paths=" not in line or "frames_in=" not in line:
            continue
        m = re.search(r"frames_in=(\d+)", line)
        if m:
            v = int(m.group(1))
            mx = v if mx is None else max(mx, v)
    return mx


def wanem_field(stats: Path, prefix, field):
    if not stats.exists():
        return None
    for line in stats.read_text().splitlines():
        if line.startswith(prefix):
            parts = line.split()
            for i, p in enumerate(parts):
                if p == field and i + 1 < len(parts):
                    try:
                        return int(parts[i + 1])
                    except ValueError:
                        return None
    return None


def main():
    work = Path(sys.argv[1])
    started = int(sys.argv[2])
    discard_rule = sys.argv[3] != "0"

    end_txt = ""
    if (work / "dmesg-end.txt").exists():
        end_txt = (work / "dmesg-end.txt").read_text(errors="replace")

    pat = re.compile(r"^\[([^\]]+)\]\s*(.*)$")
    fork_re = re.compile(r"virtual machine fork", re.I)
    balloon_re = re.compile(r"Out of puff|virtio_balloon", re.I)
    forks, balloons, lines = [], [], []
    for raw in end_txt.splitlines():
        m = pat.match(raw)
        if not m:
            continue
        ts, msg = m.group(1), m.group(2)
        unix = parse_dmesg_ts(ts)
        if unix is not None and unix < started:
            continue
        kind = None
        if fork_re.search(msg):
            kind = "fork"
            forks.append(raw)
        elif balloon_re.search(msg):
            kind = "balloon"
            balloons.append(raw)
        if kind:
            lines.append(raw)

    events = len(forks) + len(balloons)
    discarded = bool(discard_rule and events > 0)
    evidence = work / "host-pause.txt"
    body = [
        f"run_started_unix={started}",
        f"host_pause_events={events} fork={len(forks)} balloon={len(balloons)}",
        f"discard_rule=HOST_PAUSE_DISCARD={'1' if discard_rule else '0'}",
        f"discarded={'yes' if discarded else 'no'}",
        f"run_valid={'no' if discarded else 'yes'}",
        "note=event-loop stall or SUT clock_jumps alone are never a discard reason",
    ]
    if lines:
        body.extend(lines)
    else:
        body.append("no host-pause lines in the run window")
    evidence.write_text("\n".join(body) + "\n")
    print(
        f"host_pause: events={events} fork={len(forks)} balloon={len(balloons)} "
        f"discard_rule={'1' if discard_rule else '0'} discarded={'yes' if discarded else 'no'} "
        f"run_valid={'no' if discarded else 'yes'} evidence={evidence}"
    )

    stats = work / "wanem.stats"
    to_far_ok = wanem_field(stats, "to_far", "write_ok")
    to_near_ok = wanem_field(stats, "to_near", "write_ok")
    if to_far_ok is None:
        to_far_ok = wanem_field(stats, "to_far", "out")
    if to_near_ok is None:
        to_near_ok = wanem_field(stats, "to_near", "out")
    srv = None
    cli = None
    for name in ("server-mux.log", "server-bulk.log"):
        v = last_max_frames(work / name)
        if v is not None:
            srv = v if srv is None else max(srv, v)
    for name in ("client-mux.log", "client-bulk.log"):
        v = last_max_frames(work / name)
        if v is not None:
            cli = v if cli is None else max(cli, v)
    ver = mode = loss_pt = None
    if stats.exists():
        text = stats.read_text()
        m = re.search(r"wanem_version\s+(\S+)", text)
        if m:
            ver = m.group(1)
        m = re.search(r"tuple_mode\s+(\S+)", text)
        if m:
            mode = m.group(1)
        m = re.search(r"loss_point\s+(\S+)", text)
        if m:
            loss_pt = m.group(1)
    print(
        f"conservation: wanem_to_far_write_ok={to_far_ok if to_far_ok is not None else 'na'} "
        f"wanem_to_near_write_ok={to_near_ok if to_near_ok is not None else 'na'} "
        f"server_frames_in_max={srv if srv is not None else 'na'} "
        f"client_frames_in_max={cli if cli is not None else 'na'} "
        f"note=write_ok_is_packets_frames_in_is_cells_not_required_equal"
    )
    print(f"wanem_runtime: version={ver or 'na'} tuple_mode={mode or 'na'} loss_point={loss_pt or 'na'}")
    (work / "conservation.txt").write_text(
        f"wanem_to_far_write_ok={to_far_ok}\n"
        f"wanem_to_near_write_ok={to_near_ok}\n"
        f"server_frames_in_max={srv}\n"
        f"client_frames_in_max={cli}\n"
        f"wanem_version={ver}\n"
        f"tuple_mode={mode}\n"
        f"loss_point={loss_pt}\n"
    )
    if discarded:
        (work / "run-invalid").write_text("host_pause\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
