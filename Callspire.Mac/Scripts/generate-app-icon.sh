#!/usr/bin/env bash
# Regenerate AppIcon.appiconset PNGs from Callspire.Desktop/Assets/icon.png (requires Python + Pillow).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
export ROOT
python3 <<'PY'
from PIL import Image
from pathlib import Path
import os
root = Path(os.environ["ROOT"])
src = root / "Callspire.Desktop/Assets/icon.png"
out = root / "Callspire.Mac/Callspire/Assets.xcassets/AppIcon.appiconset"
out.mkdir(parents=True, exist_ok=True)
img = Image.open(src).convert("RGBA")
spec = [
    (16, "icon_16x16.png"), (32, "icon_16x16@2x.png"),
    (32, "icon_32x32.png"), (64, "icon_32x32@2x.png"),
    (128, "icon_128x128.png"), (256, "icon_128x128@2x.png"),
    (256, "icon_256x256.png"), (512, "icon_256x256@2x.png"),
    (512, "icon_512x512.png"), (1024, "icon_512x512@2x.png"),
]
for size, name in spec:
    img.resize((size, size), Image.Resampling.LANCZOS).save(out / name)
print("App icons written to", out)
PY
