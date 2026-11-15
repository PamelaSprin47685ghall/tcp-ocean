#!/usr/bin/env python3
"""Pool matched, valid segments. Report evidence; do not declare a merge decision."""
import argparse
import json
import math
from pathlib import Path
import re
import statistics


def read_runs(paths):
    runs = []
    sources = set((path / "segments.json" if path.is_dir() else path).resolve() for path in paths)
    for source in sorted(sources):
        runs.append(json.loads(source.read_text()))
    return runs


def scenario(run):
    # Omit only seed and result/provenance fields. Keep all effective network
    # settings, pre-run traffic, test durations and product switches together.
    keys = ("owd_ms", "rate_mbit", "loss", "paths", "window", "window_clamp", "seconds",
            "carrier", "profile", "uplink_pace", "client_tuple_mbit", "new_repair",
            "join_concurrency", "ping_count", "handshake_s", "pause", "skip_echo",
            "skip_iperf", "skip_ref", "server_max_paths", "wanem_version")
    result = {key: run["metadata"].get(key, "unknown") for key in keys}
    effective = {}
    for line in run.get("wanem_config", "").splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            key, value = line.lstrip().split("=", 1)
            if key != "seed":
                effective[key] = value.split()[0] if value.split() else ""
    result["wanem_config"] = effective
    result["server_extra"] = next((line for line in run.get("stamp", [])
                                    if line.startswith("server_extra=")), "unknown")
    result["stats_gap_s"] = run.get("stats_gap_s")
    result["instrumentation"] = {key: bool(run.get("requested_options", {}).get(key))
                                 for key in ("TBT_TRACE", "PING_TRACE", "ECHO_PROBE_LOG")}
    return result


def matches(pattern, value):
    return isinstance(value, str) and re.fullmatch(pattern, value) is not None


def nonnegative_integer(value, maximum=None):
    if type(value) is int:
        parsed = value
    elif matches(r"[0-9]+", value):
        try:
            parsed = int(value)
        except ValueError:
            return None
    else:
        return None
    return parsed if parsed >= 0 and (maximum is None or parsed <= maximum) else None


def normalized_seed(metadata):
    # wanem reads the value through atof before converting it to uint64_t.
    # Stay in the exact integer range of that conversion. Do not count aliases
    # such as 7 and 07 as different random sequences.
    return nonnegative_integer(metadata.get("wanem_seed", metadata.get("seed")),
                               maximum=2 ** 53 - 1)


def provenance_complete(run, record):
    bins = run.get("provenance", {})
    known_hashes = all(matches(r"[0-9a-fA-F]{64}", bins.get(label, {}).get("sha256"))
                       for label in ("server_bin", "client_bin", "client_bundle"))
    known_builds = all(matches(r"[0-9a-fA-F]{7,40}", bins.get(label, {}).get("build_commit"))
                       for label in ("server_bin", "client_bin"))
    metadata = run.get("metadata", {})
    requested = nonnegative_integer(metadata.get("paths"))
    negotiated = nonnegative_integer(run.get("negotiated", {}).get(record["session"], {}).get("paths"))
    return bool(known_hashes and known_builds
                and matches(r"[0-9a-fA-F]{7,40}(?:-dirty)?", metadata.get("script_commit"))
                and metadata.get("wanem_version") not in (None, "unknown", "na")
                and normalized_seed(metadata) is not None
                and requested is not None and requested > 0 and negotiated == requested)


def summarize(samples):
    usable, excluded, failures, observed_failures = [], [], [], []
    for run, record, metric in samples:
        seed = normalized_seed(run["metadata"])
        item = {"work": run["work"], "seed": seed, "value": record["metrics"].get(metric),
                "validity": record["validity"], "returncode": record.get("returncode"),
                "reasons": record["reasons"]}
        item["product_version"] = {label: {field: run.get("provenance", {}).get(label, {}).get(field)
                                          for field in ("sha256", "build_commit")}
                                   for label in ("server_bin", "client_bin", "client_bundle")}
        if record.get("returncode") not in (None, 0):
            observed_failures.append(item)
        if record["validity"] != "valid" or not provenance_complete(run, record):
            if not provenance_complete(run, record):
                item["reasons"] = item["reasons"] + ["provenance_incomplete"]
            excluded.append(item)
        elif not record["product_pass"]:
            failures.append(item)
        elif not isinstance(item["value"], (float, int)) or not math.isfinite(item["value"]):
            item["reasons"] = item["reasons"] + ["metric_missing"]
            excluded.append(item)
        else:
            usable.append(item)
    versions = {}
    for item in usable:
        versions.setdefault(json.dumps(item["product_version"], sort_keys=True), []).append(item)
    cohorts = []
    for version, items in versions.items():
        cohorts.append({"product_version": json.loads(version), **sample_stats(items)})
    # Two builds on one side are separate experiments. Keep their summaries but
    # do not manufacture a combined median that belongs to neither build.
    single = len(cohorts) <= 1
    result = sample_stats(usable if single else [])
    return {**result, "mixed_product_versions": not single, "cohorts": cohorts,
            "excluded": excluded, "product_failures": failures, "observed_failures": observed_failures}


def sample_stats(items):
    values = [item["value"] for item in items]
    seeds = sorted(set(str(item["seed"]) for item in items))
    quartiles = statistics.quantiles(values, method="inclusive") if len(values) >= 2 else None
    return {"n": len(values), "seeds": seeds, "median": statistics.median(values) if values else None,
            "min": min(values) if values else None, "max": max(values) if values else None,
            "range": max(values) - min(values) if values else None,
            "iqr": quartiles[2] - quartiles[0] if quartiles else None, "samples": items,
            "enough_samples": len(values) >= 2 and len(seeds) >= 2}


def compare(baseline, candidate):
    groups = {}
    for side, runs in (("baseline", baseline), ("candidate", candidate)):
        for run in runs:
            for record in run["records"]:
                metrics = ["ontime_rate", "goodput_mbit"] if record["name"] == "pingbulk" else [
                    "ontime_rate" if record["name"] == "ping" else "goodput_mbit"]
                for metric in metrics:
                    key = {"scenario": scenario(run), "segment": record["name"], "metric": metric,
                           "residual_class": record["transition"]["residual_class"]}
                    encoded = json.dumps(key, sort_keys=True)
                    group = groups.setdefault(encoded, {"key": key, "baseline": [], "candidate": []})
                    group[side].append((run, record, metric))
    result = []
    for group in groups.values():
        left, right = summarize(group["baseline"]), summarize(group["candidate"])
        class_known = group["key"]["residual_class"] not in ("unknown", "outside_reference_bands")
        enough = left["enough_samples"] and right["enough_samples"] and class_known
        delta = right["median"] - left["median"] if left["median"] is not None and right["median"] is not None else None
        result.append({**group["key"], "baseline": left, "candidate": right,
                       "median_delta": delta, "status": "review_required" if enough else "insufficient",
                       "note": "Inspect stable floor and interaction failures; a single low sample is not a veto."})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, nargs="+", required=True)
    parser.add_argument("--candidate", type=Path, nargs="+", required=True)
    args = parser.parse_args()
    print(json.dumps(compare(read_runs(args.baseline), read_runs(args.candidate)), indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
