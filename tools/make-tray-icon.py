#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Build assets/tray.ico from the pet atlas (head crop of the first idle frame)."""
import json, os
from PIL import Image

ROOT = r"C:\Users\whxnt\OneDrive\文档\deepseek-harness\default-workspace\whaledesk"
ASSETS = os.path.join(ROOT, "assets")
spec = json.load(open(os.path.join(ASSETS, "pet-spec.json"), encoding="utf-8"))
CW = spec["cell"]["width"]
CH = spec["cell"]["height"]

atlas = Image.open(os.path.join(ASSETS, "atlas.png")).convert("RGBA")
cell = atlas.crop((0, 0, CW, CH))          # row 0 = idle, frame 0
bbox = cell.getbbox()
if bbox is None:
    raise SystemExit("idle frame 0 is empty")

left, top, right, bottom = bbox
w = right - left
h = bottom - top
cx = (left + right) // 2
# Head + a bit of shoulder: the top 52% of the character, centred on the body.
head = cell.crop((max(left, cx - int(w * 0.34)), max(0, top - 2),
                  min(right, cx + int(w * 0.34)), top + int(h * 0.52)))

# Square canvas with a small margin so the icon is not edge-to-edge at 16px.
side = max(head.width, head.height)
pad = int(side * 0.10)
canvas = Image.new("RGBA", (side + pad * 2, side + pad * 2), (0, 0, 0, 0))
canvas.alpha_composite(head, ((canvas.width - head.width) // 2, (canvas.height - head.height) // 2))

sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
ico = os.path.join(ASSETS, "tray.ico")
canvas.resize((256, 256), Image.Resampling.LANCZOS).save(ico, format="ICO", sizes=sizes)
print("head bbox:", head.size, "->", ico, os.path.getsize(ico), "bytes")

# A PNG copy next to it, handy for the menu/preview.
canvas.resize((128, 128), Image.Resampling.LANCZOS).save(os.path.join(ASSETS, "tray.png"))
print("tray.png written")
