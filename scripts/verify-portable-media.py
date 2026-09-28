"""Independently decode an owned synthetic MKV. FFmpeg is a test tool, not bundled."""
from pathlib import Path
import argparse, array, json, math, subprocess
import imageio_ffmpeg

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('source', type=Path)
p.add_argument('--report', type=Path, required=True)
a = p.parse_args()
ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
decoded = subprocess.run([ffmpeg, '-v', 'error', '-i', str(a.source), '-map', '0:a:0', '-f', 's16le', '-acodec', 'pcm_s16le', '-ac', '2', '-ar', '48000', 'pipe:1'], check=True, capture_output=True, timeout=45)
samples = array.array('h', decoded.stdout)
left, right = samples[::2], samples[1::2]
assert len(left) >= 28800 and len(left) == len(right)
def level(values):
    return math.sqrt(sum((v/32768)**2 for v in values)/len(values))
def tone(values, hz):
    magnitudes = []
    for start in range(0, len(values)-2048, 2048):
        block=values[start:start+2048]
        real=sum(v*math.cos(2*math.pi*hz*i/48000) for i,v in enumerate(block))
        imag=sum(v*math.sin(2*math.pi*hz*i/48000) for i,v in enumerate(block))
        magnitudes.append(math.hypot(real,imag))
    return sum(magnitudes)/len(magnitudes)
ratios=[tone(left,440)/max(1,tone(left,880)),tone(right,880)/max(1,tone(right,440))]
assert min(level(left),level(right))>.1 and min(ratios)>5, ratios
frames=imageio_ffmpeg.read_frames(str(a.source),pix_fmt='rgb24',output_params=['-vsync','0'])
metadata=next(frames)
colours=[tuple(f[(90*320+160)*3:(90*320+160)*3+3]) for f in frames]
assert metadata['size']==(320,180) and len(colours)>=20 and len(set(colours))>=15
assert metadata['duration']>=2
result={'file':a.source.name,'video_frames':len(colours),'distinct_colours':len(set(colours)),
        'size':metadata['size'],'duration_seconds':metadata['duration'],'samples_per_channel':len(left),
        'rms':[level(left),level(right)],'stereo_tone_ratios':ratios,'decoder':'Independent FFmpeg',
        'physical_audio_devices_tested':False}
a.report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,indent=2))
