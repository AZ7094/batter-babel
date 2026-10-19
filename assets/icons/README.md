# Batter Babel 应用图标

定稿方向：**绿底（`#3FE0A8`）+ 近黑 B（`#0C1013`）**，圆角方块，不做渐变/描边/阴影。

| 文件 | 尺寸 | 用途 |
|---|---|---|
| `batter-babel.svg` | 1024 网格 | 参考文件：内嵌 `batter-babel-1024.png`，与 PNG/ICO 完全一致 |
| `batter-babel-1024.png` | 1024×1024 | 主图 / 商店图 |
| `batter-babel-512.png` | 512×512 | |
| `batter-babel-256.png` | 256×256 | |
| `batter-babel-128.png` | 128×128 | |
| `batter-babel-64.png` | 64×64 | 页面 favicon 来源（`src/public-final/icon.png`） |
| `batter-babel-48.png` | 48×48 | |
| `batter-babel-32.png` | 32×32 | |
| `batter-babel-16.png` | 16×16 | |
| `batter-babel.ico` | 16/24/32/48/64/128/256 六帧以上 | `BatterBabel.exe` 的 `ApplicationIcon` |

## 接入

- `wpf/src/BatterBabel.App/BatterBabel.App.csproj`：`<ApplicationIcon>../../../assets/icons/batter-babel.ico</ApplicationIcon>`（已接入，窗口标题栏/任务栏图标来自这里）。
- 页面：`src/public-final/icon.png`（64px 副本）+ `<link rel="icon" href="icon.png" />`。

## 生成方式（可重跑）

字形母本是 **Arial Black 的 B**（本机 `C:\Windows\Fonts\ariblk.ttf` 系统字体渲染），
1024 画布上用 600px 字号排字、按墨迹重心居中（左移 7px、上移 24px，重心落在 512,512），B 高约 428px（画布 42%），再缩到各尺寸。
不再用几何 path 手绘：手绘版在 16–40px 下笔画比例偏离参考图。

```powershell
# 1) HTML 排字 → 1024 PNG（绿底圆角 + 透明画布外缘）
msedge.exe --headless=new --default-background-color=00000000 `
  --window-size=1024,1024 --screenshot=assets\icons\batter-babel-1024.png `
  file:///<repo>/p0/.tmp/icon-render/icon.html

# 2) 多尺寸 + ICO（Pillow）
python - <<'PY'
from PIL import Image
from pathlib import Path
root = Path("assets/icons")
src = Image.open(root / "batter-babel-1024.png").convert("RGBA")
for s in [512,256,128,64,48,32,16]:
    src.resize((s, s), Image.LANCZOS).save(root / f"batter-babel-{s}.png", optimize=True)
src.save(root / "batter-babel.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
PY
```

## 已知偏差

- B 来自 Arial Black 的轮廓（系统字体），不是参考图字体本身；1024/256/64 下与参考图比例一致，16px 下仍可辨认。
- `batter-babel.svg` 是内嵌同一位图的包装文件，**不是纯矢量**；要矢量轮廓需另行描摹。
- ICO 含 256px 的 PNG 压缩帧（Windows Vista+ 支持）；XP 风格 16 色图标未提供。
