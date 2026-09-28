"""Decode the synthetic 180-frame recorder fixture; this is not a device FPS benchmark."""
import argparse
import json
import re
import subprocess
from pathlib import Path
import imageio_ffmpeg

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source', type=Path)
parser.add_argument('--report', type=Path, required=True)
args = parser.parse_args()
probe = subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), '-hide_banner', '-threads', '1',
    '-probesize', '32768', '-analyzeduration', '1000000', '-i', str(args.source),
    '-map', '0:v:0', '-vf', 'showinfo', '-fps_mode', 'passthrough', '-pix_fmt', 'rgb24',
    '-f', 'rawvideo', 'pipe:1'], capture_output=True, check=True, timeout=120)
diagnostics = probe.stderr.decode('utf-8', errors='replace')
assert len(probe.stdout) == 180 * 16 * 16 * 3
sizes = re.findall(r'\bs:(\d+x\d+)\b', diagnostics)
assert len(sizes) == 180 and set(sizes) == {'16x16'}, sizes
colours = [tuple(probe.stdout[start:start+3]) for start in range(0, len(probe.stdout), 16*16*3)]
assert len(set(colours)) >= 80
for n, colour in enumerate(colours, 1):
    assert abs(colour[0] - n) <= 5 and abs(colour[1] - 128) <= 5 and abs(colour[2] - 40) <= 5, (n, colour)
times = [float(v) for v in re.findall(r'pts_time:([\d.]+)', diagnostics)]
assert len(times) == 180 and all(b > a for a, b in zip(times, times[1:])), times
result = {'file': args.source.name, 'frames': len(colours), 'distinct_colours': len(set(colours)),
    'size': [16, 16], 'first_pts': times[0], 'last_pts': times[-1],
    'strictly_increasing_timestamps': True, 'decoder': 'Independent FFmpeg',
    'fixture_target_fps': 180, 'real_desktop_or_network_fps_measured': False}
args.report.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps(result, indent=2))
