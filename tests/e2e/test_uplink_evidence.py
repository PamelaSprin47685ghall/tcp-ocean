#!/usr/bin/env python3
"""Synthetic offline evidence tests; no network namespace or throughput claim."""
import copy
import unittest
from unittest.mock import patch
from pathlib import Path
import tempfile
import uplink_evidence as evidence


def sample(t, generation='1', control='kernel', identity='1@2', count=100):
    return {'unix': t, 'direction': 'up', 'send_control': control,
            'kernel_generation': generation, 'kernel_paths': '1',
            'kernel_path': identity + ':1000/2000/500/8000/0/400000/1000000/'
                           + str(count) + '/-/0/1/999/1000',
            **{field: str(count) for field in evidence.COUNTERS}}


def record(validity='valid'):
    return {'name': 'bulk_up', 'session': 'mux', 'direction': 'up', 'validity': validity,
            'reasons': [], 'start': {'unix_ns': 1_000_000_000},
            'end': {'unix_ns': 5_000_000_000}, 'metrics': {'goodput_mbit': 14}}


class EvidenceTests(unittest.TestCase):
    def test_same_generation_delta_and_unknown_rto(self):
        row = evidence.interval(sample(1), sample(2, count=120), 2.5)
        self.assertEqual(row['counter_deltas']['kernel_retrans'], 20)
        self.assertIsNone(row['path_counter_deltas']['1@2']['rto'])

    def test_generation_and_incarnation_changes_block_delta(self):
        for after in (sample(2, generation='2'), sample(2, identity='1@3')):
            self.assertEqual(evidence.interval(sample(1), after, 2.5)['status'], 'unavailable')

    def test_missing_generation_and_partial_topology_block_delta(self):
        for field, value in [('kernel_generation', '-'), ('kernel_paths', '2'),
                             ('kernel_path', 'malformed')]:
            after = sample(2)
            after[field] = value
            self.assertEqual(evidence.interval(sample(1), after, 2.5)['status'], 'unavailable')

    def test_reset_and_missing_not_zero(self):
        after = sample(2, count=99)
        after['kernel_rto'] = '-'
        row = evidence.interval(sample(1), after, 2.5)
        self.assertTrue(all(value is None for value in row['counter_deltas'].values()))

    def test_large_counters_preserve_integer_precision(self):
        row = evidence.interval(sample(1, count=2**60), sample(2, count=2**60 + 1), 2.5)
        self.assertEqual(row['counter_deltas']['kernel_retrans'], 1)

    def test_per_path_wrap_cannot_be_hidden_by_another_path(self):
        before, after = sample(1), sample(2)
        def entry(identity, counter):
            return identity + ':1000/2000/500/8000/0/400000/1000000/' + str(counter) + '/' + str(counter) + '/0/1/999/1000'
        for row in (before, after):
            row['kernel_paths'] = '2'
            row['kernel_retrans'] = row['kernel_rto'] = '65535'
        before['kernel_path'] = entry('1@2', 65535) + ',' + entry('2@3', 0)
        after['kernel_path'] = entry('1@2', 0) + ',' + entry('2@3', 65535)
        row = evidence.interval(before, after, 2.5)
        self.assertIsNone(row['counter_deltas']['kernel_retrans'])
        self.assertIsNone(row['counter_deltas']['kernel_rto'])
        self.assertEqual(row['path_counter_deltas']['2@3']['rto'], 65535)

    def test_gap_backwards_control_switch(self):
        for after in (sample(4), sample(0), sample(2, control='group')):
            self.assertEqual(evidence.interval(sample(1), after, 2.5)['status'], 'unavailable')

    def test_invalid_unknown_excluded_without_reclassification(self):
        for validity in ('invalid', 'unknown'):
            row = evidence.diagnose(record(validity), [sample(1), sample(2)], 2.5)
            self.assertEqual(row['validity'], validity)
            self.assertNotIn('intervals', row)

    def test_asymmetric_downlink_never_affects_uplink(self):
        up = [sample(0), sample(2), sample(3, count=120), sample(5, count=150)]
        down = {**sample(2), 'direction': 'down', 'bw_mbit': '200'}
        baseline = evidence.diagnose(record(), up, 2.5)
        mixed = evidence.diagnose(record(), up + [down], 2.5)
        self.assertEqual(baseline, mixed)
        self.assertEqual(baseline['metrics']['goodput_mbit'], 14)
        self.assertEqual(baseline['observed_window'], [2, 5])
        self.assertEqual(baseline['pre_start']['unix'], 0)
        self.assertEqual(len(baseline['first_3s']), 2)
        self.assertEqual(baseline['intervals'][0]['counter_deltas']['kernel_retrans'], 20)

    def test_analyze_keeps_mux_and_bulk_sessions_separate(self):
        mux, bulk = record(), record()
        bulk['session'] = 'bulk'
        for row in (mux, bulk):
            row['start']['iso'] = '1970-01-01T00:00:01+00:00'
        official = {'records': [mux, bulk], 'metadata': {'uplink_pace': '0'}}
        def read(path, anchor, gap):
            count = 100 if path.name == 'client-mux.log' else 500
            return {'groups': [sample(1, count=count), sample(2, count=count + 7)]}
        with patch.object(evidence.measure, 'analyze', return_value=official), \
             patch.object(evidence.measure, 'read_log', side_effect=read) as reader:
            rows = evidence.analyze(Path('/unused'))['records']
        self.assertEqual([row['session'] for row in rows], ['mux', 'bulk'])
        self.assertTrue(all(row['intervals'][0]['counter_deltas']['kernel_retrans'] == 7
                            for row in rows))
        self.assertEqual(reader.call_count, 2)

    def test_real_log_parser_preserves_paths_and_control(self):
        import datetime as dt
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'client-mux.log'
            lines = []
            for second in (1, 2):
                fields = sample(second, count=100 + second)
                line = ' '.join(str(key) + '=' + str(value) for key, value in fields.items()
                                if key not in ('unix', 'direction'))
                lines.append('00:00:0' + str(second) + '.000 stats paths=1 ' + line)
            path.write_text('\n'.join(lines))
            parsed = evidence.measure.read_log(path, dt.datetime.fromisoformat(
                '1970-01-01T00:00:01+00:00'), 2.5)
            row = evidence.diagnose(record(), parsed['groups'], 2.5)
        self.assertEqual(row['intervals'][0]['path_counter_deltas']['1@2']['retrans'], 1)

    def test_no_input_mutation_and_one_sample_insufficient(self):
        inputs = [sample(2)]
        original = copy.deepcopy(inputs)
        self.assertEqual(evidence.diagnose(record(), inputs, 2.5)['status'], 'insufficient_snapshots')
        self.assertEqual(original, inputs)


if __name__ == '__main__':
    unittest.main()
