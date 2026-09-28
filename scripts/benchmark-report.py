"""Summarize opt-in local measurements. Standard library only; never uploads data."""
import argparse
import csv
import html
import json
import math
from pathlib import Path
import statistics

FIELDS = ('case_id', 'product', 'version', 'role', 'scenario', 'device_profile',
          'network_profile', 'quality_profile', 'measurement')


def positive(value, name, allow_zero=False):
    number = float(value)
    if not math.isfinite(number) or number < 0 or (number == 0 and not allow_zero):
        raise ValueError(f'{name} must be finite and {"nonnegative" if allow_zero else "positive"}')
    return number


def p95(values):
    """Nearest-rank p95. Small samples have explicitly limited tail precision."""
    return sorted(values)[math.ceil(.95 * len(values)) - 1]


def cell(value):
    return html.escape(str(value)).replace('|', '&#124;').replace('\r', ' ').replace('\n', ' ')


def trials(path):
    groups = {}
    seen = set()
    with path.open(encoding='utf-8-sig', newline='') as source:
        reader = csv.DictReader(source)
        required = set(FIELDS) | {'trial', 'status', 'seconds', 'failure_code', 'evidence_id'}
        if not required.issubset(reader.fieldnames or []):
            raise ValueError('Trial CSV has missing columns')
        for row in reader:
            if any(not row.get(key, '').strip() for key in FIELDS + ('trial', 'status', 'evidence_id')):
                raise ValueError('Each trial needs complete case metadata and a public-safe evidence ID')
            key = tuple(row[field].strip() for field in FIELDS)
            identity = (key, row['trial'].strip())
            if identity in seen:
                raise ValueError('Duplicate trial in a comparison group')
            seen.add(identity)
            if row['status'] not in ('success', 'failure'):
                raise ValueError('Trial status must be success or failure')
            if row['status'] == 'failure':
                if row['seconds'].strip() or not row['failure_code'].strip():
                    raise ValueError('Failures require a failure code and blank connection seconds')
                elapsed = None
            else:
                if row['failure_code'].strip():
                    raise ValueError('Successful trials cannot have a failure code')
                elapsed = positive(row['seconds'], 'seconds')
            groups.setdefault(key, []).append(elapsed)
    result = []
    for key, values in sorted(groups.items()):
        successes = [v for v in values if v is not None]
        result.append(dict(zip(FIELDS, key), attempts=len(values), failures=len(values)-len(successes),
                           median=statistics.median(successes) if successes else None,
                           p95=p95(successes) if successes else None,
                           sufficient=len(values) >= 10))
    return result


def resources(path):
    manifest = json.loads(path.read_text(encoding='utf-8-sig'))
    if manifest.get('schema') != 1:
        raise ValueError('Unknown collector schema')
    for key in FIELDS[:-1]:
        if not isinstance(manifest.get(key), str) or not manifest[key].strip():
            raise ValueError(f'Missing resource metadata: {key}')
    positive(manifest['logical_cpus'], 'logical CPUs')
    positive(manifest['requested_seconds'], 'requested duration')
    actual = positive(manifest['actual_seconds'], 'actual duration', allow_zero=True)
    if manifest.get('status') not in ('complete', 'incomplete', 'interrupted'):
        raise ValueError('Unknown collection status')
    samples, valid = [], []
    last = 0.0
    with path.with_name('samples.csv').open(encoding='utf-8-sig', newline='') as source:
        reader = csv.DictReader(source)
        for row in reader:
            elapsed = positive(row['elapsed_s'], 'elapsed time')
            interval = positive(row['interval_s'], 'sample interval')
            # The initial counter snapshot is not an interval. Later boundaries must agree.
            if elapsed <= last or interval > elapsed + .01 or (samples and abs(interval - (elapsed-last)) > .01):
                raise ValueError('Overlapping, reversed or inconsistent sample times')
            last = elapsed
            status = row['status']
            if status not in ('ok', 'missing', 'unreadable', 'process_changed', 'invalid_counter'):
                raise ValueError('Unknown sample status')
            count = positive(row['process_count'], 'process count', allow_zero=True)
            if count != int(count):
                raise ValueError('Process count must be an integer')
            values = [row[k] for k in ('cpu_percent', 'working_set_mib', 'private_bytes_mib')]
            if status == 'ok':
                if count == 0:
                    raise ValueError('An empty process set is not a valid zero-use observation')
                cpu, working, private = [positive(v, 'resource value', allow_zero=True) for v in values]
                valid.append((interval, cpu, working, private))
            elif any(v.strip() for v in values):
                raise ValueError('Invalid samples must not report zero or other resource values')
            samples.append(interval)
    if len(samples) != manifest['samples'] or len(valid) != manifest['valid_samples']:
        raise ValueError('Manifest counters do not match samples')
    if last > actual + .01:
        raise ValueError('Samples extend beyond the collection duration')
    if manifest['status'] == 'complete' and (not samples or len(valid) != len(samples) or actual < manifest['requested_seconds']):
        raise ValueError('Incomplete evidence was labelled complete')
    total = sum(samples)
    valid_time = sum(v[0] for v in valid)
    manifest.update(coverage=valid_time/total if total else 0,
                    mean_cpu=sum(v[0]*v[1] for v in valid)/valid_time if valid else None,
                    p95_cpu=p95([v[1] for v in valid]) if valid else None,
                    mean_working=sum(v[0]*v[2] for v in valid)/valid_time if valid else None,
                    peak_private=max(v[3] for v in valid) if valid else None)
    return manifest


def number(value):
    return 'unavailable' if value is None else f'{value:.2f}'


def render(timing, usage):
    lines = ['# Local measurement report', '',
             'These observations do not establish product superiority. Publish the raw files and protocol with this report.', '',
             '## Connection trials', '',
             'Times are measured by the stated method. Median and nearest-rank p95 include **successful trials only**; '
             'failures remain in the attempt count. No overall latency percentile is inferred when attempts fail. '
             'Ten attempts are a minimum screening sample; at least 20 are preferable for tail estimates.', '']
    if not timing:
        lines += ['No connection trials supplied. No comparative connection claim is supported.', '']
    for group in timing:
        lines += [f'### {cell(group["product"])} {cell(group["version"])} / {cell(group["case_id"])}', '',
                  f'- Role / scenario: {cell(group["role"])} / {cell(group["scenario"])}',
                  f'- Device: {cell(group["device_profile"])}', f'- Network: {cell(group["network_profile"])}',
                  f'- Quality: {cell(group["quality_profile"])}', f'- Method: {cell(group["measurement"])}',
                  f'- Attempts: {group["attempts"]}; failures: {group["failures"]}; screening sample: '
                  f'{"at least 10" if group["sufficient"] else "INSUFFICIENT (fewer than 10)"}',
                  f'- Successful trials: median {number(group["median"])} s; p95 {number(group["p95"])} s.', '']
    lines += ['## Process resources', '',
              'CPU is normalized across logical processors. Mean CPU and working set are weighted by sample duration; '
              'p95 CPU is the nearest rank of valid interval samples. Working sets can double-count shared pages. '
              'Private bytes are committed private memory, not private resident RAM. Missing samples are excluded, never zero. '
              'A complete run covers the chosen names only; verify service/helper coverage. FPS and bandwidth are not collected.', '']
    if not usage:
        lines += ['No resource runs supplied.', '']
    for run in usage:
        lines += [f'### {cell(run["product"])} {cell(run["version"])} / {cell(run["case_id"])}', '',
                  f'- Role / scenario: {cell(run["role"])} / {cell(run["scenario"])}',
                  f'- Device: {cell(run["device_profile"])}', f'- Network: {cell(run["network_profile"])}',
                  f'- Quality: {cell(run["quality_profile"])}',
                  f'- Run: {cell(run["run_id"])}; status: **{cell(run["status"])}**; '
                  f'valid interval coverage: {100*run["coverage"]:.1f}%; elapsed: {run["actual_seconds"]:.1f} s',
                  f'- Process names: {cell(", ".join(run["process_names"]))}',
                  f'- Mean / sample p95 CPU: {number(run["mean_cpu"])}% / {number(run["p95_cpu"])}%',
                  f'- Mean working set: {number(run["mean_working"])} MiB; peak private bytes: {number(run["peak_private"])} MiB.', '']
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trials', type=Path, help='Completed trial CSV; never invent missing measurements')
    parser.add_argument('--runs', type=Path, nargs='*', default=[], help='Paths to collector run.json files')
    parser.add_argument('--output', type=Path, required=True, help='New Markdown report; existing files are preserved')
    args = parser.parse_args()
    try:
        report = render(trials(args.trials) if args.trials else [], [resources(p) for p in args.runs])
        with args.output.open('x', encoding='utf-8', newline='\n') as target:
            target.write(report)
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(1, f'Cannot produce a trustworthy report: {error}\n')
    print(f'Saved {args.output}. Nothing was uploaded.')


if __name__ == '__main__':
    main()
