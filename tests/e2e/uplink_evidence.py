#!/usr/bin/env python3
"""Read-only uplink evidence; reuse official validity, never infer throughput gates."""
import argparse
import datetime as dt
import json
from pathlib import Path

import measure_segments as measure

COUNTERS = ('kernel_retrans', 'kernel_rto', 'kernel_busy_us',
            'kernel_rwnd_us', 'kernel_sndbuf_us')
PATH_FIELDS = ('cwnd_b', 'ssthresh_b', 'unacked_b', 'send_window_b', 'notsent_b',
               'rtt_us', 'rto_us', 'retrans', 'rto', 'ca_state', 'app_limited',
               'delivery_Bps', 'pacing_Bps')


def integer(value):
    try:
        value = int(value)
        return value if value >= 0 else None
    except (ValueError, TypeError):
        return None


def paths(sample):
    result = {}
    for entry in sample.get('kernel_path', '').split(','):
        identity, sep, values = entry.partition(':')
        if not sep or '@' not in identity or identity in result:
            return None
        if any(integer(part) is None for part in identity.split('@')) or identity.count('@') != 1:
            return None
        fields = values.split('/')
        if values == '-':
            result[identity] = {field: None for field in PATH_FIELDS}
        elif len(fields) == len(PATH_FIELDS):
            result[identity] = dict(zip(PATH_FIELDS, map(integer, fields)))
        else:
            return None
    return result if len(result) == integer(sample.get('kernel_paths')) else None


def delta(before, after):
    a, b = integer(before), integer(after)
    return b - a if a is not None and b is not None and b >= a else None


def interval(before, after, gap_s):
    elapsed = after['unix'] - before['unix']
    result = {'start': before['unix'], 'end': after['unix'], 'seconds': elapsed}
    left, right = paths(before), paths(after)
    generation = integer(before.get('kernel_generation'))
    reason = None
    if not 0 < elapsed <= gap_s:
        reason = 'nonpositive_or_gap_interval'
    elif before.get('send_control') != after.get('send_control'):
        reason = 'send_control_changed'
    elif generation is None or generation != integer(after.get('kernel_generation')):
        reason = 'generation_missing_or_changed'
    elif left is None or right is None or set(left) != set(right):
        reason = 'path_identity_missing_or_changed'
    if reason:
        return {**result, 'status': 'unavailable', 'reason': reason}
    totals = {key: delta(before.get(key), after.get(key)) for key in COUNTERS}
    per_path = {identity: {key: delta(left[identity][key], right[identity][key])
                           for key in ('retrans', 'rto')} for identity in left}
    # A per-path reset/wrap can be hidden by growth on another path. Never
    # turn that cancellation into a known aggregate zero.
    for key in ('retrans', 'rto'):
        if any(values[key] is None for values in per_path.values()):
            totals['kernel_' + key] = None
    busy = totals['kernel_busy_us']
    ratios = {key + '_per_busy': (totals[key] / busy if busy and totals[key] is not None else None)
              for key in ('kernel_rwnd_us', 'kernel_sndbuf_us')}
    return {**result, 'status': 'observed', 'counter_deltas': totals,
            'limited_ratios': ratios, 'path_counter_deltas': per_path}


def diagnose(record, samples, gap_s):
    result = {key: record.get(key) for key in
              ('name', 'session', 'direction', 'validity', 'reasons', 'metrics', 'product_pass')}
    if record.get('validity') != 'valid':
        return {**result, 'status': 'excluded_by_official_validity'}
    start, end = (record[key]['unix_ns'] / 1e9 for key in ('start', 'end'))
    # Preserve log order: sorting could hide backwards clocks. Only this session's
    # client log is supplied. Do not mix server/downstream bandwidth into uplink.
    selected = [sample for sample in samples if sample['direction'] == 'up']
    prior = [sample for sample in selected if start - gap_s <= sample['unix'] <= start]
    inside = [sample for sample in selected if start <= sample['unix'] <= end]
    controls = sorted({sample.get('send_control', 'unknown') for sample in inside})
    intervals = [interval(a, b, gap_s) for a, b in zip(inside, inside[1:])]
    return {**result, 'status': 'observed' if len(inside) >= 2 else 'insufficient_snapshots',
            'send_controls': controls, 'pre_start': prior[-1] if prior else None,
            'first_3s': [sample for sample in inside if sample['unix'] <= start + 3],
            'observed_window': [inside[0]['unix'], inside[-1]['unix']] if inside else None,
            'intervals': intervals,
            'note': 'Counter intervals are inside the segment, not full-segment totals; '
                    'null means missing/reset, not zero. Kernel ACK/delivery are not application goodput.'}


def analyze(work, gap_s=2.5):
    official = measure.analyze(work, gap_s)
    rows = []
    for record in official['records']:
        if record['direction'] != 'up':
            continue
        anchor = dt.datetime.fromisoformat(record['start']['iso'])
        log = measure.read_log(work / ('client-' + record['session'] + '.log'), anchor, gap_s)
        rows.append(diagnose(record, log['groups'], gap_s))
    return {'schema': 1, 'metadata': official['metadata'],
            'provenance': official.get('provenance', {}),
            'negotiated': official.get('negotiated', {}), 'records': rows,
            'note': 'Diagnostic evidence only; no root-cause verdict or merge/throughput approval.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('work', type=Path)
    args = parser.parse_args()
    print(json.dumps(analyze(args.work), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
