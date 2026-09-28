"""Regression checks for misleading benchmark aggregation and missing evidence."""
import csv
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('benchmark_report', Path(__file__).with_name('benchmark-report.py'))
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class BenchmarkTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='lume-report-test-')
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)

    def trial(self, number=1, **changes):
        row = {key: 'synthetic' for key in report.FIELDS}
        row.update(trial=str(number), status='success', seconds=str(number), failure_code='', evidence_id='test-fixture')
        row.update(changes)
        return row

    def read_trials(self, rows):
        path = self.root / 'trials.csv'
        with path.open('w', encoding='utf-8', newline='') as target:
            writer = csv.DictWriter(target, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(rows)
        return report.trials(path)

    def read_resources(self, rows=None, **changes):
        manifest = {key: 'synthetic' for key in report.FIELDS[:-1]}
        manifest.update(schema=1, run_id='fixture', logical_cpus=4, requested_seconds=5, interval_ms=1000,
                        status='complete', samples=2, valid_samples=2, actual_seconds=5.1, process_names=['fixture'])
        manifest.update(changes)
        path = self.root / 'run.json'
        path.write_text(json.dumps(manifest), encoding='utf-8')
        header = 'elapsed_s,interval_s,status,process_count,cpu_percent,working_set_mib,private_bytes_mib\n'
        (self.root / 'samples.csv').write_text(header + (rows or '1,1,ok,1,10,20,30\n5,4,ok,1,20,40,50\n'), encoding='utf-8')
        return report.resources(path)

    def test_median_and_nearest_rank(self):
        result = self.read_trials([self.trial(i) for i in range(1, 21)])[0]
        self.assertEqual((result['median'], result['p95'], result['attempts']), (10.5, 19, 20))
        self.assertTrue(result['sufficient'])

    def test_failures_stay_in_denominator_and_are_not_zero(self):
        result = self.read_trials([self.trial(i) for i in range(1, 10)] +
                                  [self.trial(10, status='failure', seconds='', failure_code='timeout')])[0]
        self.assertEqual((result['attempts'], result['failures'], result['median']), (10, 1, 5))
        self.assertIn('successful trials only', report.render([result], []))

    def test_all_failures_have_no_latency(self):
        result = self.read_trials([self.trial(status='failure', seconds='', failure_code='timeout')])[0]
        self.assertIsNone(result['median'])
        self.assertIsNone(result['p95'])
        self.assertFalse(result['sufficient'])

    def test_quality_or_network_changes_make_separate_groups(self):
        result = self.read_trials([self.trial(), self.trial(quality_profile='other'), self.trial(network_profile='other')])
        self.assertEqual(len(result), 3)

    def test_duplicate_trials_rejected(self):
        with self.assertRaises(ValueError):
            self.read_trials([self.trial(), self.trial()])

    def test_nonfinite_negative_and_zero_timings_rejected(self):
        for value in ('NaN', 'inf', '-1', '0', ''):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.read_trials([self.trial(seconds=value)])

    def test_failed_attempt_cannot_smuggle_in_latency(self):
        with self.assertRaises(ValueError):
            self.read_trials([self.trial(status='failure', failure_code='timeout')])

    def test_duration_weighted_resources(self):
        result = self.read_resources()
        self.assertEqual((result['mean_cpu'], result['mean_working'], result['peak_private']), (18, 36, 50))

    def test_missing_samples_are_unavailable(self):
        result = self.read_resources('5,5,missing,0,,,\n', status='incomplete', samples=1, valid_samples=0)
        self.assertEqual(result['coverage'], 0)
        self.assertIsNone(result['mean_cpu'])
        self.assertIn('unavailable', report.render([], [result]))

    def test_forged_complete_status_rejected(self):
        with self.assertRaises(ValueError):
            self.read_resources('5,5,missing,0,,,\n', samples=1, valid_samples=0)

    def test_invalid_samples_cannot_be_zero_filled(self):
        with self.assertRaises(ValueError):
            self.read_resources('5,5,missing,0,0,0,0\n', status='incomplete', samples=1, valid_samples=0)

    def test_malformed_time_and_manifest_rejected(self):
        for row in ('5,5,ok,1,10,20,30\n4,1,ok,1,20,40,50\n',
                    '1,1,ok,1,NaN,20,30\n5,4,ok,1,20,40,50\n',
                    '1,1,ok,0,10,20,30\n5,4,ok,1,20,40,50\n'):
            with self.subTest(row=row), self.assertRaises(ValueError):
                self.read_resources(row)
        with self.assertRaises(ValueError):
            self.read_resources(valid_samples=1)

    def test_empty_template_does_not_invent_results(self):
        path = self.root / 'empty.csv'
        path.write_text(','.join(report.FIELDS + ('trial', 'status', 'seconds', 'failure_code', 'evidence_id')) + '\n', encoding='utf-8')
        self.assertEqual(report.trials(path), [])
        self.assertIn('No comparative connection claim', report.render([], []))

    def test_labels_are_escaped(self):
        self.assertEqual(report.cell('<script>|\n'), '&lt;script&gt;&#124; ')


if __name__ == '__main__':
    unittest.main(verbosity=2)
