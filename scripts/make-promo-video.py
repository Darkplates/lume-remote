#!/usr/bin/env python3
"""Builds the Lume promo video (1920x1080, 30 fps, silent) and a README GIF.

Usage: python3 scripts/make-promo-video.py <image-dir> <out.mp4> assets/brand.png
<image-dir> needs computers.png, computers-light.png, consent.png, files.png,
quality.png and chat.png (the Windows CI renders in docs/images/ui). Requires
ffmpeg and the Inter font (OFL) as fonts/Inter-{Bold,SemiBold,Regular}.ttf next
to this script or in LUME_VIDEO_FONTS. Fonts are not shipped with Lume.
"""
import os, subprocess, sys, shlex
IMG, OUT, LOGO = sys.argv[1], sys.argv[2], sys.argv[3]
FONT = os.environ.get('LUME_VIDEO_FONTS') or os.path.join(os.path.dirname(os.path.abspath(__file__)), 'fonts')
BOLD, SEMI, REG = [os.path.join(FONT, f) for f in ('Inter-Bold.ttf', 'Inter-SemiBold.ttf', 'Inter-Regular.ttf')]
BG, W, H, FPS, FADE = '0x12161C', 1920, 1080, 30, 0.6
work = os.path.join(os.path.dirname(OUT), 'scenes'); os.makedirs(work, exist_ok=True)

def esc(text): return text.replace('\\', '\\\\').replace(':', '\\:').replace("'", "’").replace('%', '\\%')
def caption(text, y, size, font, color='0xF3F6F8', delay=0.0):
    return (f"drawtext=fontfile={font}:text='{esc(text)}':fontsize={size}:fontcolor={color}:"
            f"x=(w-text_w)/2:y={y}:alpha='if(lt(t,{delay}),0,min(1,(t-{delay})/0.6))'")

def run(args): subprocess.run(args, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

def shot_scene(name, image, title, duration, subtitle=None):
    out = os.path.join(work, name + '.mp4')
    # Screenshot fits a 1440x760 box, with a 2 px frame, grows 4 % over the scene.
    filt = (f"[1:v]scale=w='min(1440,iw*760/ih)':h=-2,pad=iw+4:ih+4:2:2:color=0x3D4752[s];"
            f"[s]scale=w='iw*(1+0.04*t/{duration})':h=-2:eval=frame[z];"
            f"[0:v][z]overlay=x='(W-w)/2':y='(H-h)/2+70':eval=frame:shortest=1,"
            + caption(title, 64, 60, SEMI)
            + (',' + caption(subtitle, 142, 32, REG, '0xA7B0BA', 0.3) if subtitle else '')
            + f",fade=t=in:st=0:d=0.4,format=yuv420p[v]")
    run(['ffmpeg', '-y', '-f', 'lavfi', '-i', f'color=c={BG}:s={W}x{H}:r={FPS}:d={duration}', '-loop', '1', '-t', str(duration), '-i', image,
         '-filter_complex', filt, '-map', '[v]', '-r', str(FPS), '-c:v', 'libx264', '-pix_fmt', 'yuv420p', out])
    return out, duration

def card_scene(name, lines, duration, logo=False):
    out = os.path.join(work, name + '.mp4')
    parts, inputs = [], ['-f', 'lavfi', '-i', f'color=c={BG}:s={W}x{H}:r={FPS}:d={duration}']
    base = '[0:v]'
    if logo:
        inputs += ['-loop', '1', '-t', str(duration), '-i', LOGO]
        parts.append(f"[1:v]scale=200:-1[l];[0:v][l]overlay=x=(W-w)/2:y=250:shortest=1[b]"); base = '[b]'
    texts = [caption(text, y, size, font, color, delay) for (text, y, size, font, color, delay) in lines]
    parts.append(f"{base}" + ','.join(texts) + ",fade=t=in:st=0:d=0.4,format=yuv420p[v]")
    run(['ffmpeg', '-y'] + inputs + ['-filter_complex', ';'.join(parts), '-map', '[v]', '-r', str(FPS), '-c:v', 'libx264', '-pix_fmt', 'yuv420p', out])
    return out, duration

def img(name): return os.path.join(IMG, name)
scenes = [
    card_scene('01-title', [('Lume Remote', 500, 110, BOLD, '0xF3F6F8', 0.2), ('Your own PCs, one click away.', 650, 46, REG, '0xA7B0BA', 0.7)], 4.0, logo=True),
    shot_scene('02-pair', img('computers.png'), 'Pair once. Connect with one click.', 5.0, 'Saved computers, a clear status and one-time pairing codes.'),
    shot_scene('03-light', img('computers-light.png'), 'Light or dark, just like Windows.', 4.5),
    shot_scene('04-consent', img('consent.png'), 'You approve every guest.', 4.5, 'See exactly what they can do before you allow it.'),
    shot_scene('05-files', img('files.png'), 'Files both ways, verified.', 4.5, 'SHA-256 checked, existing files never overwritten.'),
    shot_scene('06-quality', img('quality.png'), 'Exact pixels or smooth video.', 4.5, 'Lossless, H.264 or low-bandwidth presets.'),
    shot_scene('07-chat', img('chat.png'), 'Chat without leaving the session.', 4.0),
    card_scene('08-values', [('No account.', 360, 84, BOLD, '0xF3F6F8', 0.1), ('No subscription.', 480, 84, BOLD, '0xF3F6F8', 0.6), ('No session timer.', 600, 84, BOLD, '0x6BE2C0', 1.1)], 4.0),
    card_scene('09-end', [('Open source  -  MIT  -  Windows 10/11', 520, 52, SEMI, '0xF3F6F8', 0.2), ('github.com/Darkplates/lume-remote', 610, 46, REG, '0x6BE2C0', 0.6), ('0.12.1 development preview', 690, 30, REG, '0xA7B0BA', 1.0)], 5.0, logo=True),
]
# Crossfade the scenes together.
inputs, filters, last, offset = [], [], '[0:v]', 0.0
for i, (path, duration) in enumerate(scenes): inputs += ['-i', path]
for i in range(1, len(scenes)):
    offset += scenes[i - 1][1] - FADE
    label = f'[x{i}]'; filters.append(f"{last}[{i}:v]xfade=transition=fade:duration={FADE}:offset={offset:.2f}{label}"); last = label
filters.append(f"{last}fade=t=out:st={offset + scenes[-1][1] - 0.8:.2f}:d=0.8,format=yuv420p[v]")
run(['ffmpeg', '-y'] + inputs + ['-filter_complex', ';'.join(filters), '-map', '[v]', '-c:v', 'libx264', '-preset', 'slow', '-crf', '20', '-movflags', '+faststart', OUT])
# A small looping GIF for the README (first 18 s, 960 px).
gif = OUT.rsplit('.', 1)[0] + '.gif'
run(['ffmpeg', '-y', '-t', '18', '-i', OUT, '-vf', 'fps=10,scale=800:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=64[p];[b][p]paletteuse=dither=bayer', gif])
print(OUT, gif)
