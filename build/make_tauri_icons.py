import os
from PIL import Image

src = "build/icon.png"
out_dir = "src-tauri/icons"
os.makedirs(out_dir, exist_ok=True)

img = Image.open(src).convert("RGBA")

# Tauri required icon sizes
sizes = {
    "32x32.png": 32,
    "128x128.png": 128,
    "128x128@2x.png": 256,
    "icon.png": 512,
}
for name, size in sizes.items():
    img.resize((size, size), Image.LANCZOS).save(os.path.join(out_dir, name))

# ico with multiple sizes
img.save(os.path.join(out_dir, "icon.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

print("icons written to", out_dir)
for f in sorted(os.listdir(out_dir)):
    print(" ", f, os.path.getsize(os.path.join(out_dir, f)))
