import os
from PIL import Image, ImageDraw, ImageFont

SIZE = 512
bg = (9, 17, 21)        # #091115
lime = (200, 255, 83)   # #c8ff53
aqua = (84, 229, 194)   # #54e5c2
dark = (16, 34, 23)     # dark text on lime

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# rounded-square background
d.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=96, fill=bg)

# subtle network nodes + lines behind the mark
cx, cy = SIZE // 2, SIZE // 2
nodes = [(64, 120), (420, 84), (452, 240), (430, 420), (96, 392), (60, 256)]
for i in range(len(nodes)):
    a = nodes[i]
    b = nodes[(i + 1) % len(nodes)]
    d.line([a, b], fill=aqua, width=6)
for x, y in nodes:
    d.ellipse([x - 10, y - 10, x + 10, y + 10], fill=aqua)

# angular cut-corner square logomark (matches the .mark clip-path)
box = 240
x0 = cx - box // 2
y0 = cy - box // 2
cut = int(box * 0.28)
poly = [
    (x0, y0),
    (x0 + box, y0),
    (x0 + box, y0 + box - cut),
    (x0 + box - cut, y0 + box),
    (x0, y0 + box),
]
d.polygon(poly, fill=lime)

# "B" letter centered on the mark
font_path = None
for p in ("C:/Windows/Fonts/segoeuib.ttf", "C:/Windows/Fonts/arialbd.ttf", "C:/Windows/Fonts/arial.ttf"):
    if os.path.exists(p):
        font_path = p
        break
font = ImageFont.truetype(font_path, 150) if font_path else ImageFont.load_default()
bbox = d.textbbox((0, 0), "B", font=font)
tw = bbox[2] - bbox[0]
th = bbox[3] - bbox[1]
d.text((cx - tw / 2 - bbox[0], cy - th / 2 - bbox[1]), "B", font=font, fill=dark)

os.makedirs("build", exist_ok=True)
img.save("build/icon.png")
img.save("build/icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("icon written: build/icon.png, build/icon.ico")
