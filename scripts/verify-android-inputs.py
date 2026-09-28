"""Match compiled JNI inputs to Gradle output and the actual APK, allowing debug stripping."""
import argparse
import hashlib
import struct
import zipfile
from pathlib import Path


def allocated_sections(data):
    assert data[:6] == b'\x7fELF\x02\x01', 'Expected little-endian ELF64'
    offset = struct.unpack_from('<Q', data, 40)[0]
    size, count, strings_index = struct.unpack_from('<HHH', data, 58)
    assert size == 64 and 0 < count <= 1024 and strings_index < count
    assert offset + size * count <= len(data)
    sections = [struct.unpack_from('<IIQQQQIIQQ', data, offset + size * n) for n in range(count)]
    strings = sections[strings_index]
    names = data[strings[4]:strings[4] + strings[5]]
    result = {}
    for section in sections:
        name_at, kind, flags, address, start, length, *_ = section
        if not flags & 2:
            continue
        assert name_at < len(names)
        name = names[name_at:].split(b'\0', 1)[0].decode('ascii')
        assert name not in result and (kind == 8 or start + length <= len(data))
        content = 'zero-filled' if kind == 8 else hashlib.sha256(data[start:start + length]).hexdigest()
        result[name] = (kind, flags, address, length, content)
    assert result, 'No allocated sections'
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('apk', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1] / 'ports/android/app'
    merged = root / 'build/intermediates/merged_native_libs/debug/mergeDebugNativeLibs/out/lib'
    stripped = root / 'build/intermediates/stripped_native_libs/debug/stripDebugDebugSymbols/out/lib'
    with zipfile.ZipFile(args.apk) as apk:
        for abi in ('arm64-v8a', 'x86_64'):
            for name in ('liblume_bridge.so', 'liblume_jni.so', 'libdatachannel.so', 'libc++_shared.so'):
                relative = Path(abi) / name
                compiled = (root / 'src/main/jniLibs' / relative).read_bytes()
                packaged = apk.read('lib/' + relative.as_posix())
                assert compiled == (merged / relative).read_bytes(), f'Stale Gradle input: {relative}'
                assert packaged == (stripped / relative).read_bytes(), f'Stale APK library: {relative}'
                assert allocated_sections(compiled) == allocated_sections(packaged), f'Runtime section changed: {relative}'
                print(f'PASS {relative.as_posix()}: compiled input = merged; allocated sections = APK; stripped output = APK')
    print('PASS eight native library identities. Execution on Android remains unverified.')
