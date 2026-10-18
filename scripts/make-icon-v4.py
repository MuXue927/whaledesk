"""Shortcut icon v4 - her WHOLE figure, clearly visible, transparent background.

v1-v3 used assets/tray.png, a 128x128 bust that is cut off at the bottom, which
is why it never looked right. This takes the full-body standing frame from the
sprite atlas instead.

C1  full body, transparent, no rim
C2  full body, transparent, thin warm rim     (transparent + still readable)
C3  full body on a very light rounded square  (max contrast anywhere)

Each .ico carries 16 24 32 48 64 128 256. At <=24 px the padding shrinks so she
stays as large as possible.
"""
from PIL import Image, ImageDraw, ImageFilter
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")
BUILD = os.path.join(ROOT, "build")
os.makedirs(BUILD, exist_ok=True)

CELL_W, CELL_H, COLS = 192, 208, 8
SIZES = [16, 24, 32, 48, 64, 128, 256]


def bbox(img):
    return img.getchannel("A").getbbox()


def opaque_score(img):
    return sum(1 for a in img.getchannel("A").getdata() if a > 180)


def best_full_body(atlas):
    best, best_score, best_where = None, -1, ""
    for row, name in enumerate(["idle", "waiting", "review", "waving"]):
        for col in range(COLS):
            cell = atlas.crop((col * CELL_W, row * CELL_H, (col + 1) * CELL_W, (row + 1) * CELL_H))
            box = bbox(cell)
            if box is None:
                continue
            height = box[3] - box[1]
            score = opaque_score(cell) + height * 60
            if score > best_score:
                best, best_score, best_where = cell, score, name + "[" + str(col) + "] h=" + str(height)
    print("chosen frame: " + best_where)
    return best


def trimmed(sprite):
    box = bbox(sprite)
    return sprite.crop(box) if box else sprite


def rim(sprite, width, color=(255, 253, 247, 205)):
    mask = sprite.getchannel("A").point(lambda a: 255 if a > 40 else 0)
    grown = mask.filter(ImageFilter.MaxFilter(2 * width + 1)).filter(ImageFilter.GaussianBlur(width * 0.5))
    layer = Image.new("RGBA", sprite.size, (0, 0, 0, 0))
    layer.paste(color, (0, 0), grown)
    layer.alpha_composite(sprite)
    return layer


def fit(sprite, size, pad_ratio):
    inner = max(1, int(size * (1 - 2 * pad_ratio)))
    w, h = sprite.size
    scale = min(inner / w, inner / h)
    return sprite.resize((max(1, round(w * scale)), max(1, round(h * scale))), Image.LANCZOS)


def centre(canvas, art, dy=0):
    canvas.alpha_composite(art, ((canvas.width - art.width) // 2,
                                 (canvas.height - art.height) // 2 + dy))


def on_canvas(art, size):
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    centre(canvas, art)
    return canvas


def light_plate(size):
    plate = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    grad = Image.new("RGBA", (size, size))
    top, bottom = (255, 255, 255, 255), (231, 240, 251, 255)
    for y in range(size):
        t = y / max(1, size - 1)
        grad.paste(tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,),
                   (0, y, size, y + 1))
    mask = Image.new("L", (size, size), 0)
    radius = max(2, round(size * 0.22))
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    plate.paste(grad, (0, 0), mask)
    edge = Image.new("L", (size, size), 0)
    ImageDraw.Draw(edge).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius,
                                           outline=255, width=max(1, size // 64))
    plate.paste((176, 198, 224, 140), (0, 0), edge)
    return plate


def save(frames, ico_name, png_name):
    frames[-1].save(os.path.join(ASSETS, ico_name), format="ICO",
                    sizes=[(f.width, f.height) for f in frames])
    frames[-1].save(os.path.join(BUILD, png_name))
    print("wrote assets/" + ico_name + " + build/" + png_name)


def main():
    atlas = Image.open(os.path.join(ASSETS, "atlas.png")).convert("RGBA")
    body = trimmed(best_full_body(atlas))
    print("full body after trim: " + str(body.size))

    c1, c2, c3 = [], [], []
    for s in SIZES:
        pad = 0.05 if s > 24 else 0.02
        art = fit(body, s, pad)
        c1.append(on_canvas(art, s))
        c2.append(on_canvas(rim(art, 1 if s <= 32 else max(1, round(s * 0.014))), s))

        plated = fit(body, s, 0.14 if s > 24 else 0.10)
        plate = light_plate(s)
        centre(plate, plated, dy=round(s * 0.01))
        c3.append(plate)

    save(c1, "whaledesk.ico", "icon-c1-transparent.png")
    save(c2, "whaledesk-clear.ico", "icon-c2-transparent-rim.png")
    save(c3, "whaledesk-plate.ico", "icon-c3-light-plate.png")

    strip = Image.new("RGBA", (900, 420), (246, 247, 250, 255))
    strip.paste(Image.new("RGBA", (900, 140), (26, 28, 34, 255)), (0, 140))
    strip.paste(Image.new("RGBA", (900, 140), (140, 152, 170, 255)), (0, 280))
    x = 20
    for size in [16, 24, 32, 48, 64, 128]:
        for row, frames in enumerate([c1, c2, c3]):
            src = next(f for f in frames if f.width == size)
            strip.alpha_composite(src, (x, 70 - size // 2 + row * 140))
        x += size + 22
    strip.save(os.path.join(BUILD, "icon-preview-strip.png"))
    print("wrote build/icon-preview-strip.png")


if __name__ == "__main__":
    main()
