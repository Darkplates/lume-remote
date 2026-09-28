"""Verify notice bytes and locked-package coverage without hiding upstream exceptions."""
from pathlib import Path
import hashlib
import json
import tomllib

root = Path(__file__).resolve().parent.parent
base = root / 'third-party' / 'portable'
inventory = json.loads((base / 'inventory.json').read_text(encoding='utf-8'))
lock = tomllib.loads((root / 'ports' / 'Cargo.lock').read_text(encoding='utf-8'))
expected = {(p['name'], p['version']): p['checksum'] for p in lock['package'] if p.get('source', '').startswith('registry+')}
seen = set()
counts = {}
files = 0
for package in inventory['packages']:
    key = (package['crate'], package['version'])
    assert key not in seen and expected.get(key) == package['checksum'], f'Unexpected locked package: {key}'
    seen.add(key)
    status = package['status']
    counts[status] = counts.get(status, 0) + 1
    assert status in ('collected', 'collected_later_upstream', 'upstream_review_required')
    assert bool(package['notices']) == (status != 'upstream_review_required'), f'Unexplained notice state: {key}'
    notices = package['notices'] + ([package['declaration']] if 'declaration' in package else [])
    for notice in notices:
        path = (base / notice['file']).resolve()
        assert path.is_relative_to(base.resolve()) and path.is_file() and not (base / notice['file']).is_symlink()
        assert hashlib.sha256(path.read_bytes()).hexdigest() == notice['sha256'], f'Notice bytes changed: {notice["file"]}'
        if notice.get('provenance_scope') == 'later-upstream-license':
            assert status == 'collected_later_upstream' and package.get('review_note')
        files += 1
assert seen == set(expected), 'Notice inventory does not cover Cargo.lock'
print(f'PASS {len(seen)} locked packages and {files} notice/declaration hashes verified.')
print(json.dumps(counts, sort_keys=True))
for package in inventory['packages']:
    if package['status'] != 'collected':
        print(f'EXPLICIT EXCEPTION {package["crate"]} {package["version"]}: {package["status"]}')
