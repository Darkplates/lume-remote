"""Independently decode synthetic fixtures. Requires test-only imageio-ffmpeg."""
from pathlib import Path
import array
import json
import math
import subprocess
import imageio_ffmpeg

root = Path(__file__).resolve().parent.parent / "verification"
source = root / "recording-audio-fixture.mp4"
ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
decoded = subprocess.run([ffmpeg, "-v", "error", "-i", str(source), "-map", "0:a:0", "-f", "f32le", "-acodec", "pcm_f32le", "-ac", "2", "-ar", "48000", "pipe:1"], capture_output=True, check=True, timeout=30)
samples = array.array("f", decoded.stdout)
left, right = samples[::2], samples[1::2]
assert len(left) >= 48000 and len(left) == len(right)


def level(values):
    return math.sqrt(sum(v*v for v in values) / len(values))


def tone(values, frequency):
    amplitudes = []
    for start in range(0, len(values)-2048, 2048):
        block = values[start:start+2048]
        real = sum(v*math.cos(2*math.pi*frequency*i/48000) for i, v in enumerate(block))
        imag = sum(v*math.sin(2*math.pi*frequency*i/48000) for i, v in enumerate(block))
        amplitudes.append(math.hypot(real, imag)/2048)
    return sum(amplitudes)/len(amplitudes)


left_rms, right_rms = level(left), level(right)
left_ratio = tone(left, 440)/max(1e-8, tone(left, 880))
right_ratio = tone(right, 880)/max(1e-8, tone(right, 440))
assert left_rms > 0.05 and right_rms > 0.03, (left_rms, right_rms)
assert left_ratio > 3 and right_ratio > 3, (left_ratio, right_ratio)
frames = imageio_ffmpeg.read_frames(str(source), pix_fmt="rgb24", output_params=["-vsync", "0"])
metadata = next(frames)
count = sum(1 for _ in frames)
assert metadata["size"] == (320, 180) and count >= 20
high_rate = max((root / "high-rate-recording").glob("rate-180-*.mp4"), key=lambda p: p.stat().st_mtime_ns)
frames = imageio_ffmpeg.read_frames(str(high_rate), pix_fmt="rgb24", output_params=["-vsync", "0"])
high_metadata = next(frames)
colours = [tuple(frame[(32*64+32)*3:(32*64+32)*3+3]) for frame in frames]
assert len(colours) == 180 and len(set(colours)) >= 170, (len(colours), len(set(colours)))
assert abs(high_metadata["duration"] - 1) < 0.02, high_metadata
assert high_metadata["size"] == (64, 64)
result = {"audio_samples_per_channel": len(left), "left_rms": left_rms, "right_rms": right_rms,
          "left_440_to_880_ratio": left_ratio, "right_880_to_440_ratio": right_ratio,
          "video_frames": count, "video_size": metadata["size"], "duration_seconds": metadata["duration"],
          "high_rate_frames": len(colours), "high_rate_distinct_colours": len(set(colours)),
          "high_rate_duration_seconds": high_metadata["duration"],
          "decoder": "independent FFmpeg, test-only", "hardware_microphone_tested": False,
          "capture_or_network_180_fps_proven": False}
(root / "v06-independent-media.json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
print(json.dumps(result, indent=2))
