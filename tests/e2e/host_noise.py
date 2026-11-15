#!/usr/bin/env python3
"""Read host pause evidence and check the quiet gate. Do not inspect SUT CPU use."""
import datetime as dt
import json
import math
from pathlib import Path
import re
import sys
import time


EVENT = re.compile(r"virtual machine fork|Out of puff|virtio_balloon", re.I)


def events_from_text(text, zone):
    events, unknown = [], []
    for line in text.splitlines():
        if not EVENT.search(line):
            continue
        match = re.match(r"^\[([^\]]+)\]\s*(.*)$", line)
        try:
            when = dt.datetime.strptime(match[1].strip(), "%a %b %d %H:%M:%S %Y")
            unix = when.replace(tzinfo=zone).timestamp()
        except (ValueError, TypeError):
            unknown.append(line)
            continue
        events.append({"start": unix, "end": unix + 1.0,
                       "kind": "fork" if "virtual machine fork" in line.lower() else "balloon",
                       "evidence": line})
    return events, unknown


def quiet_gate(text, available_kib, now, zone, quiet_s=60.0, min_mem_mib=2560.0):
    events, unknown = events_from_text(text, zone)
    reasons = []
    if not text.strip():
        reasons.append("dmesg_unavailable")
    if unknown:
        reasons.append("host_event_time_unknown")
    if any(event["start"] > now + 1.0 for event in events):
        reasons.append("host_clock_unknown")
    recent = [event for event in events if now - quiet_s <= event["end"] <= now + 1.0]
    if recent:
        reasons.append("recent_host_pause")
    if available_kib is None:
        reasons.append("memory_unavailable")
    elif available_kib < min_mem_mib * 1024:
        reasons.append("low_available_memory")
    unknown_reason = any(reason.endswith("unavailable") or reason.endswith("unknown")
                         for reason in reasons)
    return {"status": "unknown" if unknown_reason else "busy" if reasons else "quiet",
            "reasons": reasons, "checked_unix": now, "quiet_s": quiet_s,
            "min_mem_mib": min_mem_mib, "mem_available_kib": available_kib,
            "recent_events": recent, "unparsed_events": unknown}


def main():
    work = Path(sys.argv[1])
    quiet_s, min_mem_mib = map(float, sys.argv[2:4])
    bypass = sys.argv[4] == "1"
    if not all(math.isfinite(value) and value >= 0 for value in (quiet_s, min_mem_mib)):
        raise ValueError("Quiet time and memory limit must be nonnegative")
    text = (work / "dmesg-preflight.txt").read_text(errors="replace")
    memory = re.search(r"^MemAvailable:\s+(\d+)\s+kB$",
                       Path("/proc/meminfo").read_text(), re.M)
    result = quiet_gate(text, int(memory[1]) if memory else None, time.time(),
                        dt.datetime.now().astimezone().tzinfo, quiet_s, min_mem_mib)
    result["bypass"] = bypass
    (work / "host-gate.json").write_text(json.dumps(result, indent=2) + "\n")
    print(f"host_gate: status={result['status']} bypass={int(bypass)} "
          f"quiet_s={quiet_s:g} min_mem_mib={min_mem_mib:g} "
          f"mem_available_kib={result['mem_available_kib']} "
          f"reasons={','.join(result['reasons']) or 'none'}")
    return 0 if result["status"] == "quiet" or bypass else 2


if __name__ == "__main__":
    sys.exit(main())
