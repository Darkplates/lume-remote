"""Verify notice bytes and locked-package coverage without hiding upstream exceptions."""
from pathlib import Path
import hashlib
import json
import sys
import tomllib


def require(condition, message):
    """Stop with a failure. Explicit, so `python -O` cannot remove it as it removes assert."""
    if not condition:
        print(f'FAIL {message}')
        sys.exit(1)


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
    require(key not in seen and expected.get(key) == package['checksum'], f'Unexpected locked package: {key}')
    seen.add(key)
    status = package['status']
    counts[status] = counts.get(status, 0) + 1
    require(status in ('collected', 'collected_later_upstream', 'upstream_review_required'), f'Unknown notice status: {key}')
    require(bool(package['notices']) == (status != 'upstream_review_required'), f'Unexplained notice state: {key}')
    notices = package['notices'] + ([package['declaration']] if 'declaration' in package else [])
    for notice in notices:
        path = (base / notice['file']).resolve()
        require(path.is_relative_to(base.resolve()) and path.is_file() and not (base / notice['file']).is_symlink(),
                f'Notice is missing, linked or outside the inventory folder: {notice["file"]}')
        require(hashlib.sha256(path.read_bytes()).hexdigest() == notice['sha256'], f'Notice bytes changed: {notice["file"]}')
        if notice.get('provenance_scope') == 'later-upstream-license':
            require(status == 'collected_later_upstream' and package.get('review_note'),
                    f'Later-upstream licence lacks its status or review note: {key}')
        files += 1
require(seen == set(expected), 'Notice inventory does not cover Cargo.lock')
print(f'PASS {len(seen)} locked packages and {files} notice/declaration hashes verified.')
print(json.dumps(counts, sort_keys=True))
for package in inventory['packages']:
    if package['status'] != 'collected':
        print(f'EXPLICIT EXCEPTION {package["crate"]} {package["version"]}: {package["status"]}')
