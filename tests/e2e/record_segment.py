#!/usr/bin/env python3
"""Record command bounds without changing its exit status or traffic."""
import datetime as dt
import json
from pathlib import Path
import re
import sys
import time


def stamp():
    return {"unix_ns": time.time_ns(), "monotonic_ns": time.monotonic_ns(),
            "iso": dt.datetime.now().astimezone().isoformat()}


def main():
    action, folder, name = sys.argv[1:4]
    if not re.fullmatch(r"[a-z0-9_-]+", name):
        raise ValueError("Invalid segment name")
    directory = Path(folder) / "segments"
    directory.mkdir(exist_ok=True)
    path = directory / f"{name}.segment.json"
    if action == "begin":
        if path.exists():
            raise ValueError("Segment already exists: " + name)
        session, direction = sys.argv[4:6]
        if session not in ("mux", "bulk") or direction not in ("up", "down", "both"):
            raise ValueError("Invalid session or direction")
        data = {"name": name, "session": session, "direction": direction,
                "start": stamp(), "command": sys.argv[6:],
                "output": f"segments/{name}.out"}
    elif action == "end":
        data = json.loads(path.read_text())
        data.update(end=stamp(), returncode=int(sys.argv[4]))
    else:
        raise ValueError("Expected begin or end")
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    main()
