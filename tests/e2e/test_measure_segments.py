#!/usr/bin/env python3
"""Offline fixtures only. Do not start a network testbed."""
import datetime as dt
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
import compare_segments as compare
import host_noise
import measure_segments as measure


HERE = Path(__file__).resolve().parent
ANCHOR = dt.datetime.fromisoformat("2026-11-15T12:00:00+08:00")
BASE = ANCHOR.timestamp()


def record(start=1, end=3, name="bulk_up", rc=0):
    def bound(seconds):
        return {"unix_ns": int((BASE + seconds) * 1e9), "monotonic_ns": int(seconds * 1e9),
                "iso": (ANCHOR + dt.timedelta(seconds=seconds)).isoformat()}
    return {"name": name, "session": "mux", "direction": "up", "start": bound(start),
            "end": bound(end), "returncode": rc, "output": f"segments/{name}.out"}


def good_log():
    return {"heartbeats": [BASE + second for second in range(30)],
            "events": [], "groups": [], "unknown": []}


def run(seed="7", value=100, name="bulk_up", residual="kernel-unpaced"):
    row = {**record(name=name), "validity": "valid", "reasons": [], "product_pass": True,
           "metrics": {"goodput_mbit": value}, "transition": {"residual_class": residual}}
    return {"work": "run-" + seed, "metadata": {"seed": seed, "wanem_seed": seed,
             "script_commit": "abc1234", "wanem_version": "2", "paths": "64"},
            "provenance": {label: {"sha256": "a" * 64, "build_commit": "abc1234"} for label in
                           ("server_bin", "client_bin", "client_bundle")},
            "negotiated": {"mux": {"paths": "64"}}, "records": [row],
            "wanem_config": "loss=0\nseed=" + seed + "\n", "stamp": ["server_extra=''"]}


class MeasurementTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)

    def test_host_event_after_segment_does_not_discard_it(self):
        event = {"start": BASE + 8, "end": BASE + 9, "kind": "balloon"}
        result = measure.classify(record(), [good_log()], [event], [], 2.5)
        self.assertEqual(result["validity"], "valid")

    def test_host_event_overlapping_segment_is_invalid(self):
        event = {"start": BASE + 2, "end": BASE + 3, "kind": "fork"}
        result = measure.classify(record(), [good_log()], [event], [], 2.5)
        self.assertEqual(result["validity"], "invalid")
        self.assertEqual(result["reasons"], ["fork"])

    def test_missing_host_evidence_is_unknown(self):
        result = measure.classify(record(), [good_log()], [], ["host_evidence_unavailable"], 2.5)
        self.assertEqual(result["validity"], "unknown")

    def test_missing_stats_are_unknown(self):
        log = good_log()
        log["heartbeats"] = []
        self.assertEqual(measure.classify(record(), [log], [], [], 2.5)["validity"], "unknown")

    def test_no_exit_code_is_used_as_noise_evidence(self):
        self.assertEqual(measure.classify(record(rc=124), [good_log()], [], [], 2.5)["validity"], "valid")

    def test_wall_clock_step_is_invalid(self):
        row = record()
        row["end"]["unix_ns"] += 10_000_000_000
        self.assertIn("segment_clock_jump", measure.classify(row, [good_log()], [], [], 2.5)["reasons"])

    def test_gap_does_not_cross_sessions(self):
        (self.work / "segments").mkdir()
        rows = [record(1, 8, "bulk_up"), record(12, 18, "iperf_1")]
        rows[1]["session"] = "bulk"
        for row in rows:
            (self.work / "segments" / (row["name"] + ".segment.json")).write_text(json.dumps(row))
        (self.work / "dmesg-end.txt").write_text("[Sun Nov 15 11:50:00 2026] boot complete\n")
        for session, seconds in (("mux", [0, 1, 8, 9]), ("bulk", range(10, 20))):
            for side in ("client", "server"):
                prefix = "paths=4" if side == "client" else "sessions=1"
                (self.work / f"{side}-{session}.log").write_text("".join(
                    f"12:00:{second:02d}.000 stats {prefix} frames_in=1\n" for second in seconds))
        result = measure.analyze(self.work)
        outcomes = {row["name"]: row["validity"] for row in result["records"]}
        self.assertEqual(outcomes, {"bulk_up": "invalid", "iperf_1": "valid"})

    def test_clock_counter_only_invalidates_its_increment_window(self):
        path = self.work / "server-mux.log"
        path.write_text("12:00:00.000 stats sessions=1\n"
                        "12:00:00.001 stats tun clock_jumps=0\n"
                        "12:00:01.000 stats sessions=1\n"
                        "12:00:01.001 stats tun clock_jumps=2\n"
                        "12:00:02.000 stats sessions=1\n"
                        "12:00:02.001 stats tun clock_jumps=2\n"
                        "12:00:03.000 stats sessions=1\n")
        log = measure.read_log(path, ANCHOR, 2.5)
        self.assertEqual(len(log["events"]), 1)
        self.assertEqual(measure.classify(record(0.5, 1.1), [log], [], [], 2.5)["validity"], "invalid")
        self.assertEqual(measure.classify(record(1.5, 2.5), [log], [], [], 2.5)["validity"], "valid")

    def test_cpu_and_gc_counters_do_not_invalidate(self):
        path = self.work / "server-mux.log"
        path.write_text("".join(f"12:00:0{second}.000 stats sessions=1 svc_gap_us_max=800000 "
                                "gc0=400 iter_n=0 iter_us_max=900000\n" for second in range(5)))
        log = measure.read_log(path, ANCHOR, 2.5)
        self.assertEqual(measure.classify(record(), [log], [], [], 2.5)["validity"], "valid")

    def test_midnight_rollover_uses_host_date(self):
        path = self.work / "client-mux.log"
        path.write_text("23:59:59.000 stats paths=8\n00:00:00.000 stats paths=8\n")
        log = measure.read_log(path, ANCHOR.replace(hour=23), 2.5)
        self.assertEqual(log["heartbeats"][1] - log["heartbeats"][0], 1)
        self.assertEqual(log["events"], [])

    def test_first_log_can_precede_segment_anchor_midnight(self):
        path = self.work / "client-mux.log"
        path.write_text("23:59:59.000 stats paths=8\n00:00:00.000 stats paths=8\n")
        anchor = ANCHOR.replace(hour=0) + dt.timedelta(days=1, seconds=1)
        log = measure.read_log(path, anchor, 2.5)
        self.assertEqual(log["heartbeats"][0], anchor.timestamp() - 2)
        self.assertEqual(log["heartbeats"][1], anchor.timestamp() - 1)

    def test_counter_reset_outside_segment_does_not_make_it_unknown(self):
        path = self.work / "server-mux.log"
        path.write_text("".join(f"12:00:0{second}.000 stats sessions=1\n"
                                f"12:00:0{second}.001 stats tun clock_jumps={2 if second < 4 else 0}\n"
                                for second in range(6)))
        log = measure.read_log(path, ANCHOR, 2.5)
        self.assertEqual(measure.classify(record(1, 2), [log], [], [], 2.5)["validity"], "valid")

    def test_backwards_clock_is_not_midnight(self):
        path = self.work / "client-mux.log"
        path.write_text("12:00:03.000 stats paths=8\n12:00:01.000 stats paths=8\n")
        log = measure.read_log(path, ANCHOR, 2.5)
        self.assertEqual(log["events"][0]["kind"], "wall_clock_backwards")

    def test_group_continuation_inherits_timestamp_and_direction(self):
        server = self.work / "server-mux.log"
        client = self.work / "client-mux.log"
        server.write_text("12:00:00.000 stats tun clock_jumps=0\n"
                          "  group paths=8 bw_mbit=36.0 inflight=100 phase=startup\n")
        client.write_text("12:00:00.000 stats paths=8 send_control=kernel kernel_delivery_mbit=200\n")
        logs = [measure.read_log(path, ANCHOR, 2.5) for path in (server, client)]
        row = record(name="bulk_down1")
        row["direction"] = "down"
        result = measure.transition(row, logs, {"profile": "rtt400", "paths": "8"}, 2.5)
        self.assertEqual(result["residual_class"], "fast_echo_35_41")
        self.assertEqual(result["up"]["residual"]["send_control"], "kernel")
        self.assertNotIn("bw_mbit", result["up"]["residual"])

    def test_stale_downlink_sample_cannot_label_residual(self):
        log = good_log()
        log["groups"] = [{"unix": BASE, "direction": "down", "bw_mbit": "36"}]
        result = measure.transition(record(8, 10, "bulk_down1"), [log],
                                    {"profile": "rtt400", "paths": "8"}, 2.5)
        self.assertEqual(result["residual_class"], "unknown")

    def test_pingbulk_keeps_primary_ontime_and_download_rate(self):
        path = self.work / "output"
        path.write_text("ping-under-bulk planned=200 ontime_in_load=199 ontime_rate=0.995 p99_ms=420\n"
                        "bulk down seconds=85 bytes=1000 goodput_mbit=135.2\n")
        values = measure.metrics_from_output(path)
        self.assertEqual(values["ontime_rate"], 0.995)
        self.assertEqual(values["goodput_mbit"], 135.2)

    def test_nonfinite_metrics_are_not_measurements(self):
        path = self.work / "output"
        path.write_text("goodput_mbit=nan ontime_rate=inf p99_ms=-inf\n")
        self.assertEqual(measure.metrics_from_output(path), {})
        path.write_text('{"end":{"sum_received":{"bits_per_second":NaN}}}')
        self.assertEqual(measure.metrics_from_output(path), {})

    def test_host_gate_unknown_is_not_quiet(self):
        result = host_noise.quiet_gate("", 4 * 1024 * 1024, BASE, ANCHOR.tzinfo)
        self.assertEqual(result["status"], "unknown")

    def test_host_gate_checks_recent_event_and_memory(self):
        text = "[Sun Nov 15 11:59:30 2026] virtio_balloon: Out of puff\n"
        result = host_noise.quiet_gate(text, 1 * 1024 * 1024, BASE, ANCHOR.tzinfo)
        self.assertEqual(set(result["reasons"]), {"recent_host_pause", "low_available_memory"})
        self.assertEqual(result["status"], "busy")

    def test_unparseable_host_event_is_unknown(self):
        text = "[123.456] crng reseeded due to virtual machine fork\n"
        result = host_noise.quiet_gate(text, 4 * 1024 * 1024, BASE, ANCHOR.tzinfo)
        self.assertEqual(result["status"], "unknown")

    def test_two_seeds_needed_for_each_side(self):
        left, right = [run("7"), run("9")], [run("7", 101)]
        self.assertEqual(compare.compare(left, right)[0]["status"], "insufficient")
        right.append(run("9", 103))
        result = compare.compare(left, right)[0]
        self.assertEqual(result["status"], "review_required")
        self.assertEqual(result["median_delta"], 2)

    def test_repeating_one_seed_does_not_meet_seed_gate(self):
        side = [run("7"), run("7")]
        self.assertEqual(compare.compare(side, side)[0]["status"], "insufficient")

    def test_invalid_low_sample_is_kept_but_not_pooled(self):
        left = [run("7", 100), run("9", 110), run("11", 1)]
        left[-1]["records"][0]["validity"] = "invalid"
        left[-1]["records"][0]["reasons"] = ["stats_gap"]
        result = compare.compare(left, [run("7"), run("9")])[0]
        self.assertEqual(result["baseline"]["median"], 105)
        self.assertEqual(len(result["baseline"]["excluded"]), 1)

    def test_valid_product_failure_remains_visible(self):
        failed = run("7")
        failed["records"][0].update(product_pass=False, returncode=124)
        result = compare.compare([failed], [run("7")])[0]
        self.assertEqual(len(result["baseline"]["product_failures"]), 1)
        self.assertEqual(result["baseline"]["excluded"], [])

    def test_failure_in_invalid_segment_remains_visible(self):
        failed = run("7")
        failed["records"][0].update(product_pass=False, returncode=124, validity="invalid", reasons=["stats_gap"])
        result = compare.compare([failed], [run("7")])[0]["baseline"]
        self.assertEqual(len(result["observed_failures"]), 1)
        self.assertEqual(result["observed_failures"][0]["returncode"], 124)

    def test_different_binary_builds_are_not_pooled(self):
        left = [run("7", 80), run("9", 120)]
        left[1]["provenance"]["client_bundle"]["sha256"] = "b" * 64
        result = compare.compare(left, [run("7"), run("9")])[0]
        self.assertTrue(result["baseline"]["mixed_product_versions"])
        self.assertIsNone(result["baseline"]["median"])
        self.assertEqual(len(result["baseline"]["cohorts"]), 2)
        self.assertEqual(result["status"], "insufficient")

    def test_last_network_override_defines_scenario(self):
        left, right = run(), run()
        left["wanem_config"] = "loss=0\nloss=0.05\n"
        right["wanem_config"] = "loss=0.05\nloss=0\n"
        self.assertNotEqual(compare.scenario(left), compare.scenario(right))

    def test_fast_and_slow_residuals_are_not_compared(self):
        left = [run("7", name="bulk_down1", residual="slow_echo_14_18")]
        right = [run("9", name="bulk_down1", residual="fast_echo_35_41")]
        result = compare.compare(left, right)
        self.assertEqual(len(result), 2)
        self.assertTrue(all(row["status"] == "insufficient" for row in result))

    def test_unstamped_binaries_do_not_count_towards_comparison(self):
        left = [run("7"), run("9")]
        left[0]["provenance"].pop("client_bundle")
        result = compare.compare(left, [run("7"), run("9")])[0]
        self.assertEqual(result["status"], "insufficient")
        self.assertIn("provenance_incomplete", result["baseline"]["excluded"][0]["reasons"])

    def test_unknown_or_malformed_script_commit_does_not_count(self):
        for value in (None, "", "unknown", "missing", "na", "abc123", "a" * 41, "g" * 7):
            with self.subTest(value=value):
                left = [run("7"), run("9")]
                left[0]["metadata"]["script_commit"] = value
                result = compare.compare(left, [run("7"), run("9")])[0]
                self.assertEqual(result["baseline"]["n"], 1)
                self.assertEqual(result["status"], "insufficient")
                self.assertIn("provenance_incomplete", result["baseline"]["excluded"][0]["reasons"])

    def test_short_full_and_dirty_script_commits_remain_usable(self):
        for value in ("abc1234", "a" * 40, "ABC1234", "abc1234-dirty", "a" * 40 + "-dirty"):
            with self.subTest(value=value):
                item = run()
                item["metadata"]["script_commit"] = value
                self.assertTrue(compare.provenance_complete(item, item["records"][0]))

    def test_binary_hash_and_build_commit_must_have_the_right_format(self):
        for label in ("server_bin", "client_bin", "client_bundle"):
            for value in (None, "", "unknown", "missing", "na", "a" * 63, "a" * 65, "g" * 64):
                with self.subTest(label=label, hash=value):
                    item = run()
                    item["provenance"][label]["sha256"] = value
                    self.assertFalse(compare.provenance_complete(item, item["records"][0]))
        for label in ("server_bin", "client_bin"):
            for value in (None, "", "unknown", "missing", "na", "abc123", "a" * 41, "abc1234-dirty"):
                with self.subTest(label=label, commit=value):
                    item = run()
                    item["provenance"][label]["build_commit"] = value
                    self.assertFalse(compare.provenance_complete(item, item["records"][0]))

    def test_seed_aliases_do_not_count_as_two_seeds(self):
        left = [run("7"), run("07")]
        result = compare.compare(left, [run("7"), run("9")])[0]
        self.assertEqual(result["baseline"]["n"], 2)
        self.assertEqual(result["baseline"]["seeds"], ["7"])
        self.assertEqual(result["status"], "insufficient")

    def test_seed_must_be_an_exact_nonnegative_decimal_integer(self):
        for value in (None, "", "unknown", "na", "NaN", "-1", "+7", "7.0", "7e0", "0x7",
                      True, 7.0, -1, 2 ** 53, str(2 ** 53), "9007199254740993"):
            with self.subTest(value=value):
                item = run()
                item["metadata"]["wanem_seed"] = value
                result = compare.compare([item], [run()])[0]["baseline"]
                self.assertEqual(result["n"], 0)
                self.assertIn("provenance_incomplete", result["excluded"][0]["reasons"])

    def test_seed_zero_and_exact_upper_bound_remain_usable(self):
        for value in (0, "0", 7, "0007", 2 ** 53 - 1, str(2 ** 53 - 1)):
            with self.subTest(value=value):
                item = run()
                item["metadata"]["wanem_seed"] = value
                self.assertTrue(compare.provenance_complete(item, item["records"][0]))
                self.assertEqual(compare.normalized_seed(item["metadata"]), int(value))

    def test_missing_seed_is_rejected_but_requested_seed_is_a_known_fallback(self):
        item = run()
        del item["metadata"]["wanem_seed"]
        self.assertTrue(compare.provenance_complete(item, item["records"][0]))
        del item["metadata"]["seed"]
        self.assertFalse(compare.provenance_complete(item, item["records"][0]))

    def test_paths_must_be_present_positive_and_equal(self):
        for value in (None, "", "unknown", "0", "-1", "64.0", 64.0, True):
            with self.subTest(value=value):
                item = run()
                item["metadata"]["paths"] = value
                item["negotiated"]["mux"]["paths"] = value
                self.assertFalse(compare.provenance_complete(item, item["records"][0]))
        item = run()
        del item["metadata"]["paths"]
        del item["negotiated"]["mux"]["paths"]
        self.assertFalse(compare.provenance_complete(item, item["records"][0]))
        item = run()
        item["negotiated"]["mux"]["paths"] = "8"
        self.assertFalse(compare.provenance_complete(item, item["records"][0]))
        item["negotiated"]["mux"]["paths"] = 64
        self.assertTrue(compare.provenance_complete(item, item["records"][0]))

    def test_loss_percent_mistake_is_rejected_before_testbed(self):
        for value in ("5", "nan", "inf", "-0.05"):
            result = subprocess.run(["bash", str(HERE / "run.sh"), "20", "200", value, "4", "10"],
                                    text=True, capture_output=True, timeout=5)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("use 0.05 for 5%", result.stderr)

    def test_command_capture_preserves_failures_and_output(self):
        body = (HERE / "run.sh").read_text().split("run_case() {", 1)[1].split("\n}\n", 1)[0]
        script = 'here="$CASE_HERE"\nwork="$CASE_WORK"\nstatus=0\nrun_case() {' + body + '\n}\n'
        script += 'run_case test_case up python3 -c \'print("case-output"); raise SystemExit(7)\'\n'
        script += 'exit "$status"\n'
        result = subprocess.run(["bash", "-euo", "pipefail", "-c", script], text=True, capture_output=True,
                                env={**os.environ, "CASE_HERE": str(HERE), "CASE_WORK": str(self.work)})
        self.assertEqual(result.returncode, 1, result.stderr)
        row = json.loads((self.work / "segments/test_case.segment.json").read_text())
        self.assertEqual(row["returncode"], 7)
        self.assertIn("case-output", (self.work / row["output"]).read_text())
        self.assertGreater(row["end"]["monotonic_ns"], row["start"]["monotonic_ns"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
