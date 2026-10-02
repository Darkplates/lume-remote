"""Inspect the actual APK container and ELF libraries; this does not run Android."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('apk', type=Path)
args = parser.parse_args()
expected = {f'lib/{abi}/{name}' for abi in ('arm64-v8a', 'x86_64') for name in
            ('libc++_shared.so', 'libdatachannel.so', 'liblume_bridge.so', 'liblume_jni.so')}
with zipfile.ZipFile(args.apk) as package:
    names = package.namelist()
    assert len(names) == len(set(names)), 'Duplicate APK entries'
    assert package.testzip() is None, 'APK CRC failure'
    assert {n for n in names if n.startswith('lib/')} == expected, 'Unexpected native ABI/library set'
    assert 'assets/portable/inventory.json' in names, 'Upstream notice inventory missing'
    # MPL-2.0 source availability and the list of modified files travel with the APK.
    assert 'assets/THIRD-PARTY-NOTICES.txt' in names, 'THIRD-PARTY-NOTICES.txt missing from APK assets'
    repository_notices = (Path(__file__).resolve().parent.parent / 'THIRD-PARTY-NOTICES.txt').read_bytes().replace(b'\r\n', b'\n')
    assert package.read('assets/THIRD-PARTY-NOTICES.txt').replace(b'\r\n', b'\n') == repository_notices, 'APK THIRD-PARTY-NOTICES.txt differs from the repository'
    inventory = json.loads(package.read('assets/portable/inventory.json'))
    notices = 0
    for dependency in inventory['packages']:
        for notice in dependency['notices']:
            relative = PurePosixPath(notice['file'])
            assert not relative.is_absolute() and '..' not in relative.parts
            data = package.read('assets/portable/' + relative.as_posix())
            assert hashlib.sha256(data).hexdigest() == notice['sha256'], 'Changed crate notice'
            notices += 1
    rust_notice = package.read('assets/rust-stdlib-NOTICE.txt').decode('utf-8')
    rust_records = re.findall(r'^([a-f0-9]{64})  (rust-[A-Za-z0-9._+-]+)  ', rust_notice, re.MULTILINE)
    assert len(rust_records) >= 3, 'Rust standard-library provenance is incomplete'
    for expected_hash, filename in rust_records:
        assert hashlib.sha256(package.read('assets/' + filename)).hexdigest() == expected_hash, 'Changed Rust distribution notice'
    print(f'PASS {notices} crate notice files and {len(rust_records)} Rust distribution notice files match their recorded hashes.')
    for name in sorted(expected):
        entry = package.getinfo(name)
        assert entry.file_size <= 32 * 1024 * 1024, 'Unexpected native library size'
        elf = package.read(name)
        assert elf[:6] == b'\x7fELF\x02\x01', 'Expected little-endian ELF64'
        kind, machine = struct.unpack_from('<HH', elf, 16)
        assert kind == 3 and machine == (183 if '/arm64-v8a/' in name else 62), 'Wrong ELF target'
        offset = struct.unpack_from('<Q', elf, 32)[0]
        size, count = struct.unpack_from('<HH', elf, 54)
        assert size == 56 and 1 <= count <= 128 and offset + size * count <= len(elf), 'Invalid ELF program headers'
        loads = 0
        for index in range(count):
            segment, _, file_offset, address, _, file_size, _, alignment = struct.unpack_from('<IIQQQQQQ', elf, offset + index * size)
            if segment == 1:
                assert alignment >= 16384 and alignment & (alignment - 1) == 0, 'LOAD segment alignment below 16 KiB'
                assert file_offset % 16384 == address % 16384 and file_offset + file_size <= len(elf), 'Invalid LOAD segment layout'
                loads += 1
        assert loads > 0, 'No loadable ELF segments'
        print(f'PASS Packaged {name}: matching architecture and 16 KiB LOAD alignment.')
    dex = [package.read(n) for n in names if re.fullmatch(r'classes\d*\.dex', n)]
    assert dex and any(b'Lcom/lume/remote/MainActivity;' in data for data in dex), 'Main activity missing'
    for marker in (b'Lcom/lume/remote/DisplaysDialog;', b'Landroid/view/Choreographer;', b'select_monitor', b'input_ready',
                   b'Lcom/lume/remote/FolderDocuments;', b'upload_folder', b'download_folder', b'completed_directory', b'Recording FPS', b'Lcom/lume/remote/PdfPrinting;', b'annotation', b'Print PDF'):
        assert any(marker in data for data in dex), 'Portable display/folder/recording code missing from APK'
    assert all(b'Lcom/lume/remote/ContractInstrumentation;' not in data for data in dex), 'Test instrumentation leaked into app'
    assert all(b'Lkotlin/' not in data for data in dex), 'Unexpected Kotlin runtime in Java-only app'
    print('PASS App DEX excludes the separate test instrumentation and Kotlin runtime.')
    print('PASS APK entries, CRCs, ARM64/x86-64 libraries and bundled notice inventory.')
print(f'SHA256 {hashlib.file_digest(args.apk.open("rb"), "sha256").hexdigest()}')
print(f'BYTES {args.apk.stat().st_size}')
print('BOUNDARY Container/ELF inspection only; no Android installation, execution, device or WAN test.')
