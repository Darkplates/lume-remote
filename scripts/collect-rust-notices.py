"""Preserve the installed Rust standard-library notices alongside crate notices."""
from pathlib import Path
import hashlib
import subprocess

root = Path(__file__).resolve().parent.parent
sysroot = Path(subprocess.check_output(['rustc', '--print', 'sysroot'], text=True).strip())
version = subprocess.check_output(['rustc', '-vV'], text=True).strip()
source = sysroot / 'share' / 'doc' / 'rust'
copyright_file = source / 'COPYRIGHT-library.html'
assert copyright_file.is_file(), 'The matching Rust distribution standard-library notices are required'
destination = root / 'third-party'
destination.mkdir(exist_ok=True)
files = [(copyright_file, 'rust-stdlib-COPYRIGHT.html.txt')]
files += [(p, 'rust-license-' + p.name) for p in sorted((source / 'licenses').glob('*.txt'))]
assert any(name == 'rust-license-MIT.txt' for _, name in files)
assert any(name == 'rust-license-Apache-2.0.txt' for _, name in files)
records = []
for original, name in files:
    assert not original.is_symlink() and original.stat().st_size < 8 * 1024 * 1024
    data = original.read_bytes()
    output = destination / name
    if not output.exists() or output.read_bytes() != data:
        output.write_bytes(data)
    records.append(f'{hashlib.sha256(data).hexdigest()}  {name}  (Rust distribution: {original.relative_to(source).as_posix()})')
notice = '''RUST STANDARD LIBRARY - DISTRIBUTION NOTICES
The portable binaries statically include Rust standard-library code. Cargo.lock
crate notices alone do not describe that code. The accompanying copyright HTML
is copied verbatim with a .txt suffix for the release allowlist. Its references
to licenses/<name>.txt correspond to rust-license-<name>.txt in this directory.
The full upstream declarations are retained, including target-only entries;
collection is not an independent licensing audit or a claim that every entry is
linked into every application.

Compiler distribution used for this collection:
''' + version + '\n\nSHA-256 of copied distribution files:\n' + '\n'.join(records) + '\n'
output = destination / 'rust-stdlib-NOTICE.txt'
if not output.exists() or output.read_text(encoding='utf-8') != notice:
    output.write_text(notice, encoding='utf-8')
print(f'PASS Preserved {len(files)} Rust standard-library copyright/license files with distribution provenance.')
