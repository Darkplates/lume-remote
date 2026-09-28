"""Package an already tested Alpine x86-64 build without private runtime data."""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import io
import struct
import tarfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--binary', type=Path, required=True)
parser.add_argument('--peer', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
assert not args.output.exists(), 'Preserve existing archives'
assert not Path(str(args.output) + '.sha256').exists(), 'Preserve existing checksums'

def read_file(path):
    assert path.is_file() and not path.is_symlink(), 'Expected a regular release file'
    assert path.stat().st_size <= 128 * 1024 * 1024, 'Unexpected release file size'
    return path.read_bytes()

binary = read_file(args.binary)
peer = read_file(args.peer)
for name, data in [('desktop', binary), ('peer', peer)]:
    assert data[:6] == b'\x7fELF\x02\x01', f'{name}: expected little-endian ELF64'
    assert struct.unpack_from('<HH', data, 16) == (3, 62), f'{name}: expected x86-64 PIE/shared ELF'
assert b'/lib/ld-musl-x86_64.so.1\0' in binary, 'This packager is only for Alpine/musl'

readme = '''LUME REMOTE 0.12.0 - ALPINE X86-64 DEVELOPMENT BUILD
This package targets Alpine Linux 3.24.2 (musl), with an X11 desktop.
It is not a universal Linux binary, an Ubuntu binary or an AppImage.

Extract the whole archive and run ./START.sh as your normal desktop user.
Do not run the app as root. Keep libdatachannel.so beside lume-desktop.
The application is an optimized, stripped release-profile development build.

Runtime packages on the tested Alpine system include:
  libgcc libstdc++ libxcb libx11 libxrandr libxcursor libxi libxinerama
  libxkbcommon libxkbcommon-x11 libxtst libxfixes libxrender xkeyboard-config
  mesa-egl mesa-gbm mesa-gl mesa-dri-gallium
Your graphical desktop may already provide them. The owner manages OS packages.
Wayland/PipeWire support and hardware-specific configurations require separate
acceptance. This build has only been executed in an owned X11 software-rendered VM.

CONNECT
Paste a guest invitation, choose Connect, and return the P2P reply to the sharing
computer when requested. The host must approve before capture or control begins.
To share this desktop, choose Start sharing; keyboard/mouse access is an explicit
option. Invitations are private credentials. Multiple outgoing session windows
can be opened. To save a Windows host, unlock/create the local encrypted vault and
paste its one-time pairing code. Click the saved computer to reconnect. The vault
passphrase is required once per app run. Forget is local; revoke on the host to
invalidate a credential. Saved sessions automatically recover after transport loss.
Disconnect cancels recovery. Old input, clipboard and file actions are not replayed.

PERMANENT HOST
Choose Permanent access and explicitly enable access at sign-in. It requires an
unlocked Secret Service keyring and libsecret's secret-tool. The separate user
process starts after graphical login and survives dashboard closure. Pair each
controller once; revoke individual credentials or disable access here at any time.
This is not a pre-login/root service, wake mechanism or process-crash supervisor.

FILES
In an authorized session, open Files to browse, upload or download. Downloads use
the chosen local folder; existing files receive a collision-safe suffix. Transfers
verify SHA-256, show progress and support cancellation. Atomic publication requires
filesystem hard links; use an internal filesystem if a removable one rejects it.
To share local files, explicitly enable folder sharing and choose one absolute
directory before Start sharing. After approval it appears as a virtual R: drive;
other local paths and symlink traversal remain unavailable. Portable viewers can
send/receive recursive folders, including empty folders. Each retry creates a new
root; completed items survive cancellation. Saved paired single-file transfers can
resume after interruption by retrying the same unchanged file and destination.
The retained prefix and final SHA-256 are verified. Folder jobs do not resume.
Draw enables bounded annotations when connected to a compatible Windows host.
Portable hosts do not yet display remote annotations.
Print PDF makes a private copy of a completed download (up to 128 MiB), then asks
for a local CUPS printer. Install cups-client and configure the printer yourself.
Submitting a job is separate from physical printer acceptance.

MEDIA
Sound requests system audio. Voice requires local microphone consent at both ends;
the host retains a visible Stop microphone window. Install PulseAudio client tools
parec/pacat with a running compatible sound server. The app changes no global audio
settings. Media resets across reconnection. Physical audio hardware is untested.
Record creates an MKV in Downloads with JPEG images and enabled system audio,
with source/custom FPS targets in Quality. Encoding follows frame notifications;
actual FPS depends on received images and encoding speed. Microphones are excluded.
Stop finalizes the file. Recording
also stops on disconnection or resolution change. Storage can grow quickly.

QUALITY
Source negotiates the original resolution and exact PNG pixels with a compatible
host. Custom quality can reduce resolution and target FPS. Presentation wakes for
new frames through a coalescing worker; status polling stays independent. A target
of 180 FPS is not evidence of measured 180 FPS. Displays lists the host monitors;
selection releases held input and requires the new display image before input resumes.

BOUNDARIES
No commercial session-duration cap, telemetry or commercial-use detector is added.
Network/OS limits still apply. Physical WAN, hardware GPU, multi-monitor and real
desktop acceptance remain separate. Wayland input is unavailable. This is not
feature parity with the Windows implementation or a public release candidate.
Notice coverage includes all 496 locked crates: 493 exact-source collections, two
explicit later-upstream supplements and one Apple-only dispatch notice gap.
See LICENSE-PROVENANCE.md and licenses/portable/inventory.json for exact provenance.
'''
launcher = '''#!/bin/sh
set -eu
directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ] || [ ! -e /lib/ld-musl-x86_64.so.1 ]; then
  echo 'This development package requires Alpine/musl Linux on x86-64.' >&2
  exit 1
fi
if [ -z "${DISPLAY:-}" ]; then
  echo 'Open Lume from a normal X11 desktop session. See README.txt.' >&2
  exit 1
fi
test -f "$directory/libdatachannel.so" || { echo 'Extract the whole package; libdatachannel.so is missing.' >&2; exit 1; }
exec "$directory/lume-desktop"
'''
entries = {
    'lume-desktop': (binary, 0o755),
    'libdatachannel.so': (peer, 0o755),
    'START.sh': (launcher.encode(), 0o755),
    'README.txt': (readme.encode(), 0o644),
    'LICENSE.txt': (read_file(root / 'LICENSE.txt'), 0o644),
    'THIRD-PARTY-NOTICES.txt': (read_file(root / 'THIRD-PARTY-NOTICES.txt'), 0o644),
    'LICENSE-PROVENANCE.md': (read_file(root / 'docs/LICENSE-PROVENANCE.md'), 0o644),
}
notices = root / 'third-party'
for path in sorted(notices.rglob('*')):
    if path.is_file() and path.suffix.lower() in ('.txt', '.json'):
        entries['licenses/' + path.relative_to(notices).as_posix()] = (read_file(path), 0o644)
assert sum(len(data) for data, mode in entries.values()) < 256 * 1024 * 1024
manifest = ''.join(f'{hashlib.sha256(data).hexdigest()}  {name}\n' for name, (data, mode) in sorted(entries.items()))
entries['SHA256SUMS.txt'] = (manifest.encode(), 0o644)
prefix = 'LumeRemote-0.12.0-linux-alpine-x64/'
with tarfile.open(args.output, 'x:gz') as archive:
    for name, (data, mode) in sorted(entries.items()):
        path = PurePosixPath(name)
        assert not path.is_absolute() and '..' not in path.parts and '\\' not in name
        entry = tarfile.TarInfo(prefix + name)
        entry.size = len(data)
        entry.mode = mode
        archive.addfile(entry, io.BytesIO(data))

# Reopen the final bytes, verify every member, and reject links/unmanifested data.
with tarfile.open(args.output) as archive:
    members = archive.getmembers()
    assert len(members) == len(entries)
    seen = set()
    for member in members:
        assert member.isfile() and member.name.startswith(prefix)
        name = member.name[len(prefix):]
        assert name in entries and name not in seen
        seen.add(name)
        data, mode = entries[name]
        assert member.mode == mode and archive.extractfile(member).read() == data
digest = hashlib.file_digest(args.output.open('rb'), 'sha256').hexdigest()
with Path(str(args.output) + '.sha256').open('x', encoding='utf-8', newline='\n') as checksum:
    checksum.write(f'{digest}  {args.output.name}\n')
print(f'PASS {len(entries)-1} release files and manifest; no runtime settings/logs included.')
print(f'SHA256 {digest}')
print(f'BYTES {args.output.stat().st_size}')
print('BOUNDARY Alpine/musl x86-64 development only; physical desktops/WAN unverified.')
