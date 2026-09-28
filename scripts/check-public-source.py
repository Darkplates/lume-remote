"""Inspect the Git index for obvious private files/secrets before publication.

This bounded pattern check is not a complete secret/security audit. It never prints
matched values. Source fixtures can contain deliberately synthetic protocol values.
"""
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parent.parent
entries = []
for row in subprocess.check_output(['git', '-C', str(root), 'ls-files', '--stage', '-z']).decode('utf-8').split('\0'):
    if not row: continue
    metadata, name = row.split('\t', 1)
    mode, blob, stage = metadata.split()
    assert stage == '0' and mode in ('100644', '100755'), 'Unresolved stage or linked source entry'
    entries.append((name, blob))
assert entries, 'Stage the intended source files first'
blobs = subprocess.check_output(['git', '-C', str(root), 'cat-file', '--batch'], input=('\n'.join(blob for _, blob in entries) + '\n').encode('ascii'))
offset = 0
forbidden = re.compile(r'(^|/)(host\.dat|computers\.dat|.*\.(pfx|pem|key|keystore)|local\.properties|\.env(?:\..*)?|verification|target|\.gradle|jniLibs|\.git)(/|$)', re.I)
tokens = [
    re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    re.compile(rb'\bgh[pousr]_[A-Za-z0-9]{30,}\b'),
    re.compile(rb'\bgithub_pat_[A-Za-z0-9_]{30,}\b'),
    re.compile(rb'\bAIza[A-Za-z0-9_-]{30,}\b'),
]
failures = []
for name, blob in entries:
    if forbidden.search(name): failures.append((name, 'private/generated path'))
    end = blobs.index(b'\n', offset)
    actual, kind, length = blobs[offset:end].split()
    assert actual.decode('ascii') == blob and kind == b'blob'
    offset = end + 1
    staged = blobs[offset:offset + int(length)]
    offset += int(length) + 1
    if len(staged) > 10 * 1024 * 1024: failures.append((name, 'unexpected source size'))
    if any(rule.search(staged) for rule in tokens): failures.append((name, 'secret-shaped value'))
    if name.endswith(('.md', '.txt', '.json', '.yml')) and not name.startswith('third-party/'):
        if re.search(rb'C:[/\\]Users[/\\](?!Public\b|Default\b|<|example\b|owner\b)[^/\\\r\n ]+', staged, re.I):
            failures.append((name, 'personal Windows profile path'))
for name, reason in failures: print(f'FAIL {name}: {reason}')
assert not failures, 'Public source checks found entries requiring review'
print(f'PASS {len(entries)} staged files checked; no matching private paths or secret patterns.')
print('BOUNDARY Pattern inspection, not proof that all possible secrets or vulnerabilities are absent.')
