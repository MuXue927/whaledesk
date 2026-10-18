#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""P0: WebP -> PNG atlas, plus a contact sheet and an automated cell-bleed check.

The contact sheet is the visual proof that the 8x9 grid slicing is correct; the
bleed check catches the case where a sprite crosses its 192x208 cell boundary
(which would mean the grid assumption is wrong even though every count matches).
"""
import json, os
from PIL import Image, ImageDraw, ImageFont

ROOT = r"C:\Users\whxnt\OneDrive\文档\deepseek-harness\default-workspace\whaledesk"
ASSETS = os.path.join(ROOT, "assets")
BUILD = os.path.join(ROOT, "build")
os.makedirs(BUILD, exist_ok=True)

spec = json.load(open(os.path.join(ASSETS, "pet-spec.json"), encoding="utf-8"))
CW, CH = spec["cell"]["width"], spec["cell"]["height"]
COLS, ROWS = spec["columns"], spec["atlasRows"]
tracks = list(spec["tracks"].keys())

atlas = Image.open(os.path.join(ASSETS, "atlas.webp")).convert("RGBA")
print("atlas:", atlas.size, "expected:", (CW * COLS, CH * ROWS))
assert atlas.size == (CW * COLS, CH * ROWS), "atlas size mismatch"

png_path = os.path.join(ASSETS, "atlas.png")
atlas.save(png_path, "PNG", optimize=True)
print("saved", png_path, os.path.getsize(png_path), "bytes")

alpha = atlas.getchannel("A")
bleed_rows = []
cell_boxes = []
for i, name in enumerate(tracks):
    n = spec["rows"][i]
    for j in range(n):
        box = (j * CW, i * CH, (j + 1) * CW, (i + 1) * CH)
        cell = alpha.crop(box)
        bbox = cell.getbbox()
        cell_boxes.append((name, j, bbox))
        if bbox is None:
            bleed_rows.append(f"{name}[{j}] EMPTY CELL")
            continue
        left, top, right, bottom = bbox
        if left == 0 or top == 0 or right == CW or bottom == CH:
            bleed_rows.append(f"{name}[{j}] touches cell edge bbox={bbox}")

print("cell bleed problems:", bleed_rows if bleed_rows else "NONE")

# --- contact sheet: label column + one column per frame index ---
SCALE = 0.5
tw, th = int(CW * SCALE), int(CH * SCALE)
LBL_W, HDR_H = 190, 30
W = LBL_W + COLS * tw
H = HDR_H + ROWS * th
sheet = Image.new("RGBA", (W, H), (24, 26, 38, 255))
d = ImageDraw.Draw(sheet)
try:
    f = ImageFont.load_default(size=13)
    fb = ImageFont.load_default(size=14)
except TypeError:
    f = fb = ImageFont.load_default()

for j in range(COLS):
    d.text((LBL_W + j * tw + 6, 9), f"col {j}", fill=(150, 160, 190), font=f)

for i, name in enumerate(tracks):
    y = HDR_H + i * th
    n = spec["rows"][i]
    t = spec["tracks"][name]
    d.text((8, y + 6), f"{i} {name}", fill=(240, 240, 255), font=fb)
    d.text((8, y + 26), f"{n}f  {t['durations'][0]}ms", fill=(140, 150, 180), font=f)
    d.text((8, y + 44), "loop" if t.get("loop") else f"→{t.get('fallback','?')}", fill=(140, 150, 180), font=f)
    for j in range(n):
        cell = atlas.crop((j * CW, i * CH, (j + 1) * CW, (i + 1) * CH)).resize((tw, th), Image.Resampling.LANCZOS)
        x = LBL_W + j * tw
        # checkerboard so transparency is visible
        for cy in range(0, th, 16):
            for cx in range(0, tw, 16):
                if (cx // 16 + cy // 16) % 2 == 0:
                    d.rectangle([x + cx, y + cy, x + min(cx + 15, tw - 1), y + min(cy + 15, th - 1)], fill=(44, 47, 62, 255))
        sheet.alpha_composite(cell, (x, y))
        d.rectangle([x, y, x + tw - 1, y + th - 1], outline=(90, 95, 120, 255))

sheet_path = os.path.join(BUILD, "p0-contact-sheet.png")
sheet.convert("RGB").save(sheet_path, "PNG")
print("saved", sheet_path, os.path.getsize(sheet_path), "bytes", sheet.size)

json.dump(
    {"cellBleed": bleed_rows, "cells": [{"track": a, "frame": b, "bbox": c} for a, b, c in cell_boxes]},
    open(os.path.join(BUILD, "p0-slice-check.json"), "w", encoding="utf-8"), indent=1)
print("empty cells:", sum(1 for _, _, b in cell_boxes if b is None), "/", len(cell_boxes))
