#!/usr/bin/env python3
"""Local tests for ping / pingbulk reporting. No netns."""
import importlib.util
import io
import socket
import sys
import threading
import time
import unittest
from contextlib import redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('echo', HERE / 'echo.py')
echo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(echo)


def _free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class EchoChecks(unittest.TestCase):
    def setUp(self):
        self.port = _free_port()
        self.server = threading.Thread(target=echo.serve, args=(self.port,), daemon=True)
        self.server.start()
        deadline = time.time() + 2
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.1):
                    return
            except OSError:
                time.sleep(0.02)
        self.fail("echo server did not start")

    def test_idle_ping_all_ok(self):
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = echo.ping("127.0.0.1", self.port, 20)
        line = buf.getvalue().strip()
        self.assertEqual(rc, 0)
        self.assertIn("ok=20", line)
        self.assertIn("timeout=0", line)
        self.assertIn("fail=0", line)
        self.assertIn("denom=20", line)
        self.assertIn("under_load=20", line)
        self.assertIn("p99_ms=", line)
        self.assertNotIn("p99_ms=na", line)

    def test_pingbulk_runs_until_probes_finish_and_p99_is_under_load(self):
        buf = io.StringIO()
        t0 = time.time()
        with redirect_stdout(buf):
            rc = echo.pingbulk("127.0.0.1", self.port, 30)
        dt = time.time() - t0
        out = buf.getvalue()
        self.assertEqual(rc, 0)
        # 30 probes * 20 ms spacing is ~0.6 s plus RTT; must not be capped by a short bulk.
        self.assertGreater(dt, 0.5)
        self.assertIn("ping-under-bulk", out)
        self.assertIn("ok=30", out)
        self.assertIn("denom=30", out)
        self.assertIn("under_load=30", out)
        self.assertIn("(p99 over probes fully inside load)", out)
        self.assertIn("bulk down", out)

    def test_timeout_counted_in_denominator(self):
        # A probe timeout is recorded; p99 uses only successful in-load samples.
        records = [
            {"status": "ok", "inside_load": True, "rtt_ms": 10.0},
            {"status": "ok", "inside_load": True, "rtt_ms": 12.0},
            {"status": "timeout", "inside_load": True, "rtt_ms": 2000.0},
            {"status": "ok", "inside_load": False, "rtt_ms": 8.0},
            {"status": "fail", "inside_load": True, "rtt_ms": 1.0},
        ]
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = echo._report_ping("ping-under-bulk", 5, records)
        line = buf.getvalue().strip()
        self.assertEqual(rc, 1)
        self.assertIn("ok=3", line)
        self.assertIn("timeout=1", line)
        self.assertIn("fail=1", line)
        self.assertIn("denom=5", line)
        self.assertIn("under_load=2", line)
        self.assertIn("p99_ms=12.0", line)
        self.assertNotIn("2000", line)

    def test_p99_na_when_no_in_load_sample(self):
        records = [{"status": "ok", "inside_load": False, "rtt_ms": 10.0}]
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = echo._report_ping("ping-under-bulk", 1, records)
        self.assertEqual(rc, 0)
        self.assertIn("p99_ms=na", buf.getvalue())


if __name__ == "__main__":
    unittest.main(verbosity=2)
