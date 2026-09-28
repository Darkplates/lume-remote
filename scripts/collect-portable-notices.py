"""Collect upstream notices from the exact downloaded Cargo.lock registry crates.

This is provenance collection, not an independent licence or security audit.
Missing target-only packages remain explicit in the inventory.
"""
from pathlib import Path
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
import re
import tomllib
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--fetch-upstream', action='store_true', help='Fetch missing notices from the exact GitHub commit recorded in each published crate')
args = parser.parse_args()

root = Path(__file__).resolve().parent.parent
cargo = Path(os.environ.get("CARGO_HOME", Path.home() / ".cargo"))
registries = sorted((cargo / "registry" / "src").glob("*"))
output = root / "third-party" / "portable"
output.mkdir(parents=True, exist_ok=True)
records = []
previous_path = output / 'inventory.json'
previous = json.loads(previous_path.read_text(encoding='utf-8')) if previous_path.exists() else {'packages': []}
previous = {(p['crate'], p['version']): p for p in previous['packages']}
upstream_cache = {}
upstream_names = ['LICENSE', 'LICENSE.txt', 'LICENSE.md', 'LICENSE-MIT', 'LICENSE-APACHE', 'COPYING', 'LICENCE', 'LICENSE_1_0.txt', 'LICENSE-MIT.txt', 'LICENSE-APACHE.txt', 'LICENSES/MIT.txt']

def fetch_notice(url):
    try:
        with urlopen(Request(url, headers={'User-Agent': 'Lume-notice-collector/0.7'}), timeout=15) as response:
            content = response.read(1024 * 1024 + 1)
            if len(content) > 1024 * 1024 or b'\0' in content:
                raise ValueError('Unexpected upstream notice content')
            content.decode('utf-8')
            return content
    except HTTPError as error:
        if error.code == 404:
            return None
        print(f'Notice fetch unavailable: HTTP {error.code} for {url}')
    except (URLError, TimeoutError):
        print(f'Notice fetch unavailable: {url}')
    return None

def save_notice(record, relative, content, **origin):
    destination = output / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists() or destination.read_bytes() != content:
        destination.write_bytes(content)
    record['notices'].append({'file': relative, 'sha256': hashlib.sha256(content).hexdigest(), **origin})

lock = tomllib.loads((root / "ports" / "Cargo.lock").read_text(encoding="utf-8"))
for entry in lock["package"]:
    if not entry.get("source", "").startswith("registry+"):
        continue
    slug = entry["name"] + "-" + entry["version"]
    if not re.fullmatch(r"[A-Za-z0-9_.+-]+", slug):
        raise ValueError("Unexpected crate identifier")
    source = next((p / slug for p in registries if (p / slug / "Cargo.toml").is_file()), None)
    record = {"crate": entry["name"], "version": entry["version"], "checksum": entry["checksum"], "source": "https://crates.io/crates/" + entry["name"] + "/" + entry["version"], "license": None, "notices": []}
    if source is not None:
        package = tomllib.loads((source / "Cargo.toml").read_text(encoding="utf-8"))["package"]
        record["license"] = package.get("license")
        candidates = [p for p in source.iterdir() if p.is_file() and re.match(r"(?i)^(license|licence|copying|copyright|notice)([._-]|$)", p.name)]
        root_notice = bool(candidates)
        # These font notices ship inside the checksum-verified crate, not its root.
        if entry['name'] == 'epaint_default_fonts':
            candidates.extend((source / 'fonts').glob('*.txt'))
        explicit = package.get("license-file")
        if explicit:
            path = (source / explicit).resolve()
            if path.is_relative_to(source.resolve()) and path.is_file():
                candidates.append(path)
        for path in sorted(set(candidates)):
            if path.is_symlink() or path.stat().st_size > 1024 * 1024:
                raise ValueError("Unexpected upstream notice file")
            content = path.read_bytes()
            inside = path.relative_to(source).as_posix()
            relative = slug + "/" + inside.replace('/', '__') + ".txt"
            save_notice(record, relative, content, crate_path=inside)
        if not root_notice:
            # Preserve previously collected, hash-checked provenance in offline runs.
            old = previous.get((entry['name'], entry['version']), {})
            if old.get('checksum') == entry['checksum']:
                for notice in old.get('notices', []):
                    relative = notice['file']
                    if 'upstream_url' not in notice or not re.fullmatch(re.escape(slug) + r'/UPSTREAM-[A-Za-z0-9_.+-]+\.txt', relative):
                        continue
                    saved = output / relative
                    if saved.is_file() and not saved.is_symlink():
                        content = saved.read_bytes()
                        if hashlib.sha256(content).hexdigest() == notice['sha256']:
                            record['notices'].append(notice)
            if args.fetch_upstream and not any('upstream_url' in n for n in record['notices']):
                repository = re.match(r'https?://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)', package.get('repository', ''))
                vcs_file = source / '.cargo_vcs_info.json'
                vcs = json.loads(vcs_file.read_text(encoding='utf-8')) if vcs_file.exists() else {}
                commit = vcs.get('git', {}).get('sha1', '')
                if repository and re.fullmatch(r'[0-9a-f]{40}', commit):
                    owner, repository_name = repository.groups()
                    repository_name = repository_name.removesuffix('.git')
                    base = f'https://raw.githubusercontent.com/{owner}/{repository_name}/{commit}/'
                    if base not in upstream_cache:
                        with ThreadPoolExecutor(max_workers=4) as workers:
                            upstream_cache[base] = list(zip(upstream_names, workers.map(fetch_notice, [base+n for n in upstream_names])))
                    for name, content in upstream_cache[base]:
                        if content:
                            save_notice(record, slug+'/UPSTREAM-'+name.replace('/', '__')+'.txt', content, upstream_url=base+name, commit=commit)
    old = previous.get((entry['name'], entry['version']), {})
    if old.get('checksum') == entry['checksum']:
        for field in ('review_note', 'selected_license'):
            if field in old: record[field] = old[field]
        declaration = old.get('declaration')
        if declaration:
            relative = declaration.get('file', '')
            if re.fullmatch(re.escape(slug) + r'/UPSTREAM-[A-Za-z0-9_.+-]+\.txt', relative):
                saved = output / relative
                if saved.is_file() and not saved.is_symlink() and hashlib.sha256(saved.read_bytes()).hexdigest() == declaration['sha256']:
                    record['declaration'] = declaration
    record["status"] = "collected" if source and record["notices"] else "upstream_review_required"
    if any(n.get('provenance_scope') == 'later-upstream-license' for n in record['notices']):
        record['status'] = 'collected_later_upstream'

    records.append(record)
manifest = {"scope": "Locked workspace registry dependencies, including optional and target-only crates. Not every entry is linked into every binary.", "independent_review": "pending", "packages": records}
inventory = output / "inventory.json"
serialized = json.dumps(manifest, indent=2) + "\n"
if not inventory.exists() or inventory.read_text(encoding="utf-8") != serialized:
    inventory.write_text(serialized, encoding="utf-8")
collected = sum(p["status"] in ("collected", "collected_later_upstream") for p in records)
print(f"Collected upstream notices for {collected}/{len(records)} locked registry packages. See inventory.json for explicit gaps.")
