"""Batter Babel promo — PPT-style slides + synthesized background music.

Short and plain on purpose: each slide is a static image, slides fade into one another,
and the music is generated with numpy (no third-party audio, so nothing to license).

Output: media/batter-babel-promo.mp4 and .gif
"""

import os
import subprocess
import sys
import wave

import numpy as np
from PIL import Image, ImageDraw, ImageFont

W, H, FPS = 1280, 720, 30
SR = 44100
OUT = r"D:\Batter Babel\media"
TMP = os.path.join(OUT, "_tmp")
SHOT = os.path.join(OUT, "app-window.png")

BG = (9, 17, 21)
PANEL = (23, 31, 38)
LIME = (200, 255, 83)
AQUA = (84, 229, 194)
INK = (233, 240, 245)
MUTED = (138, 152, 163)
AMBER = (255, 196, 84)

FONTS = r"C:\Windows\Fonts"


def f(name, size):
    try:
        return ImageFont.truetype(os.path.join(FONTS, name), size)
    except Exception:
        return ImageFont.load_default()


F_TITLE = f("msyhbd.ttc", 84)
F_SUB = f("msyh.ttc", 30)
F_H = f("msyhbd.ttc", 52)
F_B = f("msyhbd.ttc", 30)
F_S = f("msyh.ttc", 25)


def new_slide():
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    # subtle frame so slides do not look flat
    d.rectangle([0, 0, W - 1, H - 1], outline=(26, 36, 44), width=2)
    d.rectangle([40, 40, W - 41, H - 41], outline=(18, 26, 32), width=1)
    return img, d


def ctext(d, y, text, font, fill):
    box = d.textbbox((0, 0), text, font=font)
    d.text(((W - (box[2] - box[0])) // 2, y), text, font=font, fill=fill)


def draw_logo(img, cx, cy, size):
    d = ImageDraw.Draw(img)
    r = size // 2
    d.rounded_rectangle([cx - r, cy - r, cx + r, cy + r], radius=size // 5, fill=LIME)
    ft = f("msyhbd.ttc", int(size * 0.62))
    box = d.textbbox((0, 0), "B", font=ft)
    d.text((cx - (box[2] - box[0]) / 2 - box[0], cy - (box[3] - box[1]) / 2 - box[1]),
           "B", font=ft, fill=(16, 34, 23))


def slide_title():
    img, d = new_slide()
    draw_logo(img, W // 2, 230, 160)
    ctext(d, 350, "Batter Babel", F_TITLE, INK)
    ctext(d, 466, "游戏网络优选 · 开源免费", F_SUB, AQUA)
    return img


def slide_ui():
    img, d = new_slide()
    ctext(d, 62, "自动识别已安装的联网游戏", F_H, INK)
    shot = Image.open(SHOT).convert("RGB").crop((0, 30, 795, 677))
    avail_w, avail_h = int(W * 0.62), H - 190
    sc = min(avail_w / shot.width, avail_h / shot.height)
    s = shot.resize((int(shot.width * sc), int(shot.height * sc)), Image.LANCZOS)
    x, y = (W - s.width) // 2, 165
    d.rectangle([x + 5, y + 8, x + s.width + 5, y + s.height + 8], fill=(0, 0, 0))
    img.paste(s, (x, y))
    d.rectangle([x, y, x + s.width, y + s.height], outline=(40, 54, 64), width=1)
    return img


def slide_features():
    img, d = new_slide()
    ctext(d, 66, "它做什么", F_H, INK)
    rows = [
        ("线路优选", "2000 个 Cloudflare 地址实测下载速度，最快的写入 hosts", LIME),
        ("加速游戏", "本机 QoS（DSCP 46）提升游戏进程优先级，改善对局延迟", AQUA),
        ("系统调优", "TCP / 网卡 / 节流一键优化，随时可完整还原", AMBER),
    ]
    for i, (t, s, c) in enumerate(rows):
        y = 210 + i * 140
        d.rounded_rectangle([110, y, W - 110, y + 112], radius=14,
                            fill=PANEL, outline=c, width=2)
        d.rectangle([110, y, 118, y + 112], fill=c)
        d.text((150, y + 20), t, font=F_B, fill=c)
        d.text((150, y + 64), s, font=F_S, fill=INK)
    return img


def slide_stats():
    img, d = new_slide()
    ctext(d, 120, "完全本地 · 开源", F_H, INK)
    stats = [("1.1 MB", "安装包"), ("MIT", "许可证"), ("0", "云端服务")]
    for i, (big, small) in enumerate(stats):
        cx = W // 2 + (i - 1) * 340
        d.rounded_rectangle([cx - 145, 260, cx + 145, 430], radius=16,
                            fill=PANEL, outline=LIME, width=2)
        box = d.textbbox((0, 0), big, font=F_TITLE)
        d.text((cx - (box[2] - box[0]) / 2, 288), big, font=F_TITLE, fill=LIME)
        box2 = d.textbbox((0, 0), small, font=F_S)
        d.text((cx - (box2[2] - box2[0]) / 2, 386), small, font=F_S, fill=MUTED)
    ctext(d, 500, "不接管 DNS · 不创建代理 · 不修改路由表", F_SUB, AQUA)
    return img


def slide_outro():
    img, d = new_slide()
    draw_logo(img, W // 2, 200, 130)
    ctext(d, 310, "github.com/AZ7094/batter-babel", F_H, LIME)
    ctext(d, 400, "免费下载 · 源码开放 · Windows 10 / 11", F_SUB, INK)
    ctext(d, 470, "对局延迟请用「加速游戏」", F_S, MUTED)
    return img


# ---------------------------------------------------------------- music

def adsr(n, a=0.01, d=0.1, s=0.7, r=0.2):
    env = np.ones(n)
    ai, di, ri = int(n * a), int(n * d), int(n * r)
    si = max(0, n - ai - di - ri)
    parts = []
    if ai:
        parts.append(np.linspace(0, 1, ai))
    if di:
        parts.append(np.linspace(1, s, di))
    if si:
        parts.append(np.full(si, s))
    if ri:
        parts.append(np.linspace(s, 0, ri))
    env = np.concatenate(parts)[:n]
    if len(env) < n:
        env = np.pad(env, (0, n - len(env)))
    return env


def tone(freq, dur, amp=0.2, kind="sine", detune=0.0):
    n = int(SR * dur)
    t = np.arange(n) / SR
    if kind == "sine":
        w = np.sin(2 * np.pi * freq * t)
    elif kind == "tri":
        w = 2 * np.abs(2 * (t * freq - np.floor(t * freq + 0.5))) - 1
    else:  # soft saw
        w = 2 * (t * freq - np.floor(t * freq + 0.5))
    if detune:
        w = 0.6 * w + 0.4 * np.sin(2 * np.pi * freq * (1 + detune) * t)
    return amp * w * adsr(n)


def kick(dur=0.28, amp=0.5):
    n = int(SR * dur)
    t = np.arange(n) / SR
    freq = 120 * np.exp(-t * 22) + 42
    w = np.sin(2 * np.pi * np.cumsum(freq) / SR)
    return amp * w * np.exp(-t * 9)


def hat(dur=0.06, amp=0.12):
    n = int(SR * dur)
    t = np.arange(n) / SR
    return amp * np.random.default_rng(7).normal(0, 1, n) * np.exp(-t * 70)


def build_music(duration):
    total = int(SR * duration)
    buf = np.zeros(total + SR)

    # Am - F - C - G, one bar each, 8 s loop
    bar = 2.0
    chords = [
        (220.00, 261.63, 329.63),   # Am
        (174.61, 220.00, 261.63),   # F
        (261.63, 329.63, 392.00),   # C
        (196.00, 246.94, 293.66),   # G
    ]
    pad_amp, mel_amp = 0.055, 0.11
    melody = [659.25, 587.33, 523.25, 587.33, 440.00, 523.25, 587.33, 659.25]

    pos = 0.0
    bar_i = 0
    step = 0
    while pos < duration:
        ch = chords[bar_i % 4]
        # pad chord
        seg = np.zeros(int(SR * bar) + SR)
        for fr in ch:
            seg[: len(tone(fr, bar, pad_amp, "tri"))] += tone(fr, bar, pad_amp, "tri")
        # arpeggio on eighths
        for k in range(4):
            fr = ch[k % 3] * (2 if k == 3 else 1)
            v = tone(fr, bar / 4, mel_amp * 0.55, "sine", detune=0.004)
            off = int(SR * (bar / 4) * k)
            seg[off:off + len(v)] += v
        # melody on beats
        for k in range(2):
            fr = melody[(step + k) % len(melody)]
            v = tone(fr, bar / 2, mel_amp, "sine", detune=0.002)
            off = int(SR * (bar / 2) * k)
            seg[off:off + len(v)] += v
        step += 2
        # drums
        for k in range(4):
            off = int(SR * 0.5 * k)
            kk = kick()
            seg[off:off + len(kk)] += kk * (0.9 if k in (0, 2) else 0.5)
            hh = hat()
            seg[off + int(SR * 0.25):off + int(SR * 0.25) + len(hh)] += hh
        end = int(SR * pos) + len(seg)
        if end > len(buf):
            buf = np.pad(buf, (0, end - len(buf)))
        buf[int(SR * pos):end] += seg[: end - int(SR * pos)]
        pos += bar
        bar_i += 1

    buf = buf[:total]
    # gentle low-pass to soften the saw/tri edges
    k = np.ones(24) / 24
    buf = np.convolve(buf, k, mode="same")
    # fade in / out
    fi, fo = int(SR * 0.8), int(SR * 1.4)
    buf[:fi] *= np.linspace(0, 1, fi)
    buf[-fo:] *= np.linspace(1, 0, fo)
    peak = np.max(np.abs(buf)) or 1.0
    buf = buf / peak * 0.55
    return buf


def write_wav(path, mono):
    data = np.int16(np.clip(mono, -1, 1) * 32767)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data.tobytes())


# ---------------------------------------------------------------- render

def main():
    os.makedirs(TMP, exist_ok=True)
    for x in os.listdir(TMP):
        os.remove(os.path.join(TMP, x))

    slides = [slide_title(), slide_ui(), slide_features(), slide_stats(), slide_outro()]
    slide_dir = os.path.join(OUT, "slides")
    os.makedirs(slide_dir, exist_ok=True)
    for i, s in enumerate(slides):
        s.save(os.path.join(slide_dir, f"slide{i + 1}.png"))

    hold = 2.8          # seconds per slide
    fade = 0.35         # cross-fade
    per = hold + fade
    idx = 0
    n_slides = len(slides)
    for i in range(n_slides):
        frames = int(per * FPS)
        for k in range(frames):
            t = k / FPS
            img = slides[i].copy()
            if t < fade and i > 0:
                a = t / fade
                img = Image.blend(slides[i - 1], img, a)
            elif t > hold and i < n_slides - 1:
                a = (t - hold) / fade
                img = Image.blend(img, slides[i + 1], a * 0.5)
            img.save(os.path.join(TMP, f"f{idx:05d}.png"))
            idx += 1

    dur = idx / FPS
    print(f"frames: {idx}  duration: {dur:.1f}s")

    wav = os.path.join(TMP, "music.wav")
    write_wav(wav, build_music(dur))
    print("music:", os.path.getsize(wav) // 1024, "KB")

    import imageio_ffmpeg
    ff = imageio_ffmpeg.get_ffmpeg_exe()
    out = os.path.join(OUT, "batter-babel-promo.mp4")
    cmd = [
        ff, "-y",
        "-framerate", str(FPS), "-i", os.path.join(TMP, "f%05d.png"),
        "-i", wav,
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-preset", "medium",
        "-c:a", "aac", "-b:a", "128k", "-shortest",
        "-movflags", "+faststart", out,
    ]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stderr[-2500:])
        sys.exit(1)
    print("video:", out, f"{os.path.getsize(out) / 1024:.0f} KB")

    # silent GIF for README / social
    gif = os.path.join(OUT, "batter-babel-promo.gif")
    r2 = subprocess.run([
        ff, "-y", "-i", out, "-vf",
        "fps=10,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3",
        "-loop", "0", gif,
    ], capture_output=True, text=True)
    if r2.returncode == 0:
        print("gif:", gif, f"{os.path.getsize(gif) / 1024:.0f} KB")


if __name__ == "__main__":
    main()
