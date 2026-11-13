#!/usr/bin/env python3
"""Offline tests only. Namespace deletion and process signals are mocked."""
import importlib.util
import sys
sys.dont_write_bytecode = True
from pathlib import Path
import signal
import subprocess
import tempfile
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('tb_guard', HERE / 'tb_guard.py')
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class GuardChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.base, self.netns = root / 'state', root / 'netns'
        self.base.mkdir()
        self.netns.mkdir()
        self.scope = guard.Scope(self.base, self.netns, 'boot-test')
        self.names = ['tbe_near', 'tbe_wan', 'tbe_far']

    def create_owned(self):
        self.scope.begin(self.names)
        for name in self.names:
            (self.netns / name).touch()
            self.scope.record_namespace(name)

    def call_down(self, runner, pids=b''):
        def pids_or_run(*a, **k):
            # subprocess.check_output(['ip','netns','pids', name]) or run(['ip','netns','del', name])
            argv = a[0] if a else k.get('args')
            if argv and argv[:3] == ['ip', 'netns', 'pids']:
                return pids
            return runner(*a, **k) if runner else subprocess.CompletedProcess(argv, 0)

        with mock.patch.object(guard, 'protected_base'), \
                mock.patch.object(guard, 'Scope', return_value=self.scope), \
                mock.patch.object(guard.subprocess, 'run', side_effect=pids_or_run), \
                mock.patch.object(guard.subprocess, 'check_output', side_effect=pids_or_run):
            return guard.main(['down'])

    def test_existing_namespace_is_not_adopted(self):
        (self.netns / 'tbe_wan').touch()
        with self.assertRaises(RuntimeError):
            self.scope.begin(self.names)
        self.assertFalse(self.scope.file.exists())
        self.assertTrue((self.netns / 'tbe_wan').exists())

    def test_no_manifest_means_no_cleanup(self):
        (self.netns / 'tbe_near').touch()
        calls = []
        self.call_down(lambda *a, **k: calls.append(a))
        self.assertEqual(calls, [])
        self.assertTrue((self.netns / 'tbe_near').exists())

    def test_namespace_replacement_stops_all_cleanup(self):
        self.create_owned()
        (self.netns / 'tbe_wan').rename(self.netns / 'old-wan')
        (self.netns / 'tbe_wan').touch()
        calls = []
        with self.assertRaises(RuntimeError):
            self.call_down(lambda *a, **k: calls.append(a))
        self.assertEqual(calls, [])
        self.assertTrue(self.scope.file.exists())

    def test_exact_recorded_cleanup_and_repeat(self):
        self.create_owned()
        (self.netns / 'other').touch()
        calls = []

        def delete(argv, **kwargs):
            self.assertEqual(argv[:3], ['ip', 'netns', 'del'])
            self.assertIn(argv[3], self.names)
            calls.append(argv[3])
            (self.netns / argv[3]).unlink()
            return subprocess.CompletedProcess(argv, 0)

        self.call_down(delete)
        self.assertEqual(calls, self.names)
        self.assertTrue((self.netns / 'other').exists())
        self.call_down(delete)
        self.assertEqual(calls, self.names)

    def test_partial_setup_only_removes_recorded_namespace(self):
        self.scope.begin(self.names)
        (self.netns / 'tbe_near').touch()
        self.scope.record_namespace('tbe_near')
        (self.netns / 'tbe_far').touch()  # No ownership record: must remain untouched.
        calls = []

        def delete(argv, **kwargs):
            calls.append(argv[3])
            (self.netns / argv[3]).unlink()
            return subprocess.CompletedProcess(argv, 0)

        self.call_down(delete)
        self.assertEqual(calls, ['tbe_near'])
        self.assertTrue((self.netns / 'tbe_far').exists())

    def test_pid_reuse_never_signals(self):
        self.scope.begin(self.names)
        state = self.scope.load()
        state['processes']['wanem'] = {'pid': 12345, 'start': 'old'}
        self.scope.save(state)
        with mock.patch.object(guard.os, 'pidfd_open', return_value=97), \
                mock.patch.object(guard.os, 'close'), \
                mock.patch.object(guard, 'process_start', return_value=('new', 100, 'S')), \
                mock.patch.object(guard.signal, 'pidfd_send_signal') as send:
            with self.assertRaises(RuntimeError):
                self.scope.signal_process('wanem', signal.SIGTERM, wait=True)
            send.assert_not_called()
        self.assertIn('wanem', self.scope.load()['processes'])

    def test_owned_process_uses_pidfd_and_releases_record(self):
        self.scope.begin(self.names)
        state = self.scope.load()
        state['processes']['wanem'] = {'pid': 12345, 'start': 'owned'}
        self.scope.save(state)
        poller = mock.Mock()
        poller.poll.return_value = [(97, 1)]
        with mock.patch.object(guard.os, 'pidfd_open', return_value=97), \
                mock.patch.object(guard.os, 'close'), \
                mock.patch.object(guard, 'process_start', return_value=('owned', 100, 'S')), \
                mock.patch.object(guard.select, 'poll', return_value=poller), \
                mock.patch.object(guard.signal, 'pidfd_send_signal') as send:
            self.scope.signal_process('wanem', signal.SIGTERM, wait=True)
            send.assert_called_once_with(97, signal.SIGTERM)
        self.assertNotIn('wanem', self.scope.load()['processes'])

    def test_boot_change_rejected(self):
        self.scope.begin(self.names)
        self.scope.boot = 'different-boot'
        with self.assertRaises(RuntimeError):
            self.scope.load()

    def test_foreign_child_cannot_be_registered(self):
        self.scope.begin(self.names)
        with mock.patch.object(guard, 'process_start', return_value=('42', 7, 'S')):
            with self.assertRaises(RuntimeError):
                self.scope.record_process('wanem', 12345, 8)
        self.assertEqual(self.scope.load()['processes'], {})

    def test_up_script_never_deletes_before_begin(self):
        text = (HERE / 'netns.sh').read_text()
        self.assertNotIn('down; up', text)
        # `up` must not call down, and must go through the guard before any netns add/del.
        # A leading unconditional `ip netns del` of the fixed names is the v2.2 violation.
        up_fn = text.split('up() {', 1)[1].split('\n}', 1)[0]
        self.assertIn('begin $NEAR $WAN $FAR', up_fn)
        self.assertNotIn('ip netns del', up_fn)

    def test_shell_syntax(self):
        for name in ('netns.sh', 'run.sh'):
            subprocess.run(['bash', '-n', str(HERE / name)], check=True)


if __name__ == '__main__':
    print('Offline mocks and bash syntax only. No network testbed is run.', flush=True)
    unittest.main(verbosity=2)
