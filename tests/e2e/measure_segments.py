#!/usr/bin/env python3
"""Classify each measured command by its own time window and tunnel session.

Keep product failures separate from missing or contaminated measurements. A busy
event loop is not evidence of host noise. This module never changes test verdicts.
"""
import argparse
import datetime as dt
import json
import math
from pathlib import Path
import re

from host_noise import events_from_text


FIELDS = re.compile(r"\b([a-zA-Z][a-zA-Z0-9_]*)=([^\s]+)")
TIME = re.compile(r"^(\d{2}:\d{2}:\d{2}\.\d+)\s+(.*)$")


def overlaps(start, end, event):
    return start < event["end"] and event["start"] < end


def number(value):
    try:
        parsed = float(value)
        return parsed if math.isfinite(parsed) else None
    except (TypeError, ValueError):
        return None


def read_log(path, anchor, gap_s):
    result = {"source": path.name, "heartbeats": [], "events": [], "groups": [],
              "unknown": [], "uncertain": []}
    if not path.exists():
        result["unknown"].append("log_missing:" + path.name)
        return result
    date = anchor.date()
    last = current = first = None
    clock_sample = None
    clock_count = 0
    for raw in path.read_text(errors="replace").splitlines():
        match = TIME.match(raw)
        if match:
            now = dt.datetime.combine(date, dt.time.fromisoformat(match[1]), anchor.tzinfo)
            # A large backwards time-of-day step is midnight. Smaller steps are
            # clock changes; they must not become an invented extra day.
            if last is None:
                now = min((now + dt.timedelta(days=offset) for offset in (-1, 0, 1)),
                          key=lambda value: abs(value.timestamp() - anchor.timestamp()))
                date = now.date()
            elif now.timestamp() < last - 12 * 3600:
                date += dt.timedelta(days=1)
                now += dt.timedelta(days=1)
            current = now.timestamp()
            if last is not None and current < last:
                result["events"].append({"start": current, "end": last,
                                         "kind": "wall_clock_backwards", "log": path.name})
            last = current
            if first is None:
                first = current
            body = match[2]
        elif raw.startswith("  ") and current is not None:
            body = raw.strip()
        else:
            continue
        values = dict(FIELDS.findall(body))
        if body.startswith("stats sessions=") or (
                path.name.startswith("client-") and body.startswith("stats paths=")):
            result["heartbeats"].append(current)
        if "clock_jumps" in values:
            count = int(values["clock_jumps"])
            if count > clock_count:
                result["events"].append({"start": clock_sample if clock_sample is not None else first,
                                         "end": current, "kind": "clock_jump", "log": path.name,
                                         "count": count - clock_count})
            elif count < clock_count:
                result["uncertain"].append({"start": clock_sample, "end": current,
                                            "kind": "clock_counter_reset", "log": path.name})
            clock_sample, clock_count = current, count
        if path.name.startswith("server-") and body.startswith("group paths="):
            result["groups"].append({"unix": current, "direction": "down", **values})
        elif path.name.startswith("client-") and "send_control" in values:
            group = {key[5:]: value for key, value in values.items() if key.startswith("pace_")}
            group.update({key: value for key, value in values.items() if key.startswith("kernel_")})
            result["groups"].append({"unix": current, "direction": "up", **group,
                                     "send_control": values["send_control"], "stats": body})
        elif path.name.startswith("client-") and "pace_bw_mbit" in values:
            group = {key[5:]: value for key, value in values.items() if key.startswith("pace_")}
            result["groups"].append({"unix": current, "direction": "up", **group,
                                     "send_control": "group", "stats": body})
    for before, after in zip(result["heartbeats"], result["heartbeats"][1:]):
        if after - before > gap_s:
            result["events"].append({"start": before, "end": after, "kind": "stats_gap",
                                     "seconds": after - before, "log": path.name})
    return result


def residual_class(bandwidth, metadata, name):
    if name != "bulk_down1" or metadata.get("profile") != "rtt400" or metadata.get("paths") != "8":
        return "not_classed"
    if bandwidth is None:
        return "unknown"
    if 14 <= bandwidth <= 18:
        return "slow_echo_14_18"
    if 35 <= bandwidth <= 41:
        return "fast_echo_35_41"
    return "outside_reference_bands"


def transition(record, logs, metadata, gap_s):
    start = record["start"]["unix_ns"] / 1e9
    result = {}
    for direction in ("up", "down"):
        samples = sorted((sample for log in logs for sample in log["groups"]
                          if sample["direction"] == direction), key=lambda sample: sample["unix"])
        before = [sample for sample in samples if start - gap_s <= sample["unix"] <= start]
        residual = before[-1] if before else None
        trace = [{"offset_s": round(sample["unix"] - start, 3), **sample}
                 for sample in samples if start <= sample["unix"] <= start + 3]
        result[direction] = {"residual": residual, "first_3s": trace,
                             "status": "observed" if residual else "unavailable"}
        generations = {sample.get("kernel_generation") for sample in ([residual] if residual else []) + trace
                       if sample.get("kernel_generation") is not None}
        result[direction]["kernel_generation_changed"] = len(generations) > 1
    bandwidth = number((result["down"]["residual"] or {}).get("bw_mbit"))
    result["residual_class"] = residual_class(bandwidth, metadata, record["name"])
    if record["direction"] == "up" and metadata.get("uplink_pace") == "0":
        result["residual_class"] = "kernel-unpaced"
    return result


def metrics_from_output(path):
    if not path.exists():
        return {}
    text = path.read_text(errors="replace")
    try:
        value = json.loads(text)
        rate = number(value["end"]["sum_received"]["bits_per_second"])
        return {"goodput_mbit": rate / 1e6} if rate is not None else {}
    except (ValueError, KeyError, TypeError):
        pass
    wanted = {"goodput_mbit", "ontime_rate", "planned", "ontime_in_load", "p50_ms",
              "p95_ms", "p99_ms", "max_ms", "seconds", "bytes"}
    return {key: float(value) for key, value in FIELDS.findall(text)
            if key in wanted and number(value) is not None}


def classify(record, logs, host_events, evidence_unknown, gap_s):
    unknown = list(evidence_unknown)
    if "end" not in record:
        return {"validity": "unknown", "reasons": ["segment_did_not_finish"], "events": []}
    start = record["start"]["unix_ns"] / 1e9
    end = record["end"]["unix_ns"] / 1e9
    if end <= start:
        return {"validity": "invalid", "reasons": ["segment_clock_backwards"], "events": []}
    events = [event for event in host_events if overlaps(start, end, event)]
    for log in logs:
        unknown.extend(log["unknown"])
        unknown.extend(event["kind"] + ":" + event["log"] for event in log.get("uncertain", [])
                       if overlaps(start, end, event))
        events.extend(event for event in log["events"] if overlaps(start, end, event))
        beats = log["heartbeats"]
        if not beats:
            unknown.append("stats_missing:" + log.get("source", "unknown"))
        elif min(beats) > start + gap_s or max(beats) < end - gap_s:
            unknown.append("stats_do_not_cover_segment:" + log.get("source", "unknown"))
    wall = end - start
    elapsed = (record["end"]["monotonic_ns"] - record["start"]["monotonic_ns"]) / 1e9
    if abs(wall - elapsed) > gap_s:
        events.append({"start": start, "end": end, "kind": "segment_clock_jump",
                       "wall_s": wall, "monotonic_s": elapsed})
    return {"validity": "invalid" if events else "unknown" if unknown else "valid",
            "reasons": sorted(set([event["kind"] for event in events] + unknown)), "events": events}


def analyze(work, gap_s=2.5):
    stamp = (work / "stamp.txt").read_text(errors="replace") if (work / "stamp.txt").exists() else ""
    metadata = {}
    for line in stamp.splitlines():
        if line.startswith(("stamp:", "flags:")):
            metadata.update(FIELDS.findall(line))
        elif line.startswith("wanem_runtime:"):
            runtime = dict(FIELDS.findall(line))
            metadata["wanem_version"] = runtime.get("version", "unknown")
    records = [json.loads(path.read_text()) for path in sorted((work / "segments").glob("*.segment.json"))]
    if not records:
        return {"schema": 1, "work": str(work.resolve()), "metadata": metadata,
                "records": [], "status": "no_segment_records"}
    anchor = dt.datetime.fromisoformat(min(records, key=lambda record: record["start"]["unix_ns"])["start"]["iso"])
    dmesg = (work / "dmesg-end.txt").read_text(errors="replace") if (work / "dmesg-end.txt").exists() else ""
    host_events, unknown_events = events_from_text(dmesg, anchor.tzinfo)
    evidence_unknown = []
    if not dmesg.strip():
        evidence_unknown.append("host_evidence_unavailable")
    if unknown_events:
        evidence_unknown.append("host_event_time_unknown")
    sessions = {session: [read_log(work / f"{side}-{session}.log", anchor, gap_s)
                          for side in ("client", "server")] for session in ("mux", "bulk")}
    for record in records:
        logs = sessions[record["session"]]
        record.update(classify(record, logs, host_events, evidence_unknown, gap_s))
        record["metrics"] = metrics_from_output(work / record["output"])
        record["transition"] = transition(record, logs, metadata, gap_s)
        record["product_pass"] = record.get("returncode") == 0
    gate = json.loads((work / "host-gate.json").read_text()) if (work / "host-gate.json").exists() else None
    provenance = {}
    for label in ("server_bin", "client_bin", "client_apphost", "client_bundle"):
        found = next((line for line in stamp.splitlines() if line.startswith(label + ":")), "")
        provenance[label] = dict(FIELDS.findall(found))
    negotiated = {match[1]: dict(FIELDS.findall(match[2])) for match in
                  re.finditer(r"^negotiated\((\w+)\):\s*(.*)$", stamp, re.M)}
    config = (work / "wanem.conf").read_text() if (work / "wanem.conf").exists() else ""
    options = json.loads((work / "run-options.json").read_text()) if (work / "run-options.json").exists() else {}
    return {"schema": 1, "work": str(work.resolve()), "metadata": metadata,
            "provenance": provenance, "negotiated": negotiated, "stamp": stamp.splitlines(),
            "wanem_config": config, "requested_options": options, "host_gate": gate,
            "stats_gap_s": gap_s, "records": records}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("work", type=Path)
    parser.add_argument("--stats-gap-s", type=float, default=2.5)
    args = parser.parse_args()
    if not math.isfinite(args.stats_gap_s) or args.stats_gap_s <= 0:
        parser.error("--stats-gap-s must be positive")
    result = analyze(args.work, args.stats_gap_s)
    (args.work / "segments.json").write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n")
    for record in result["records"]:
        print(f"segment: name={record['name']} session={record['session']} "
              f"validity={record['validity']} rc={record.get('returncode', 'unfinished')} "
              f"residual_class={record['transition']['residual_class']} "
              f"reasons={','.join(record['reasons']) or 'none'}")
    print(f"segments: path={args.work / 'segments.json'} count={len(result['records'])}")


if __name__ == "__main__":
    main()
