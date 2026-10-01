"""Scoutsu app icon, built to Microsoft's Windows app icon guidelines.

  - One literal metaphor (magnifying glass = search); the osu! cue is that the
    glass is a hit circle (white ring, pink centre). No decoration beyond that.
  - Monochrome pink ramp around the app accent #FF66AB, subtle 2-step gradients
    at 120 degrees, ambient light from the top left. No gloss, no glow, and no
    shadow cast onto the background.
  - Rim and handle pinks pass 3:1 contrast on both light and dark taskbars.
  - Geometry lives on a 48x48 grid. 16/20/24/32 are laid out per size on whole
    pixels (snapped edges, integer strokes) instead of shrunk from 256.

Run from anywhere:  python branding/make_icon.py   (needs Pillow + numpy)
Writes OsuScoutNew/Assets/scoutsu.ico (what the app and installer use) and,
next to this script, scoutsu_256.png and review.png (every size on light and
dark, plus 16/24/32 zoomed on the pixel grid - check it after any change).
"""
import math, os
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = os.path.dirname(os.path.abspath(__file__))
ICO = os.path.join(OUT, "..", "OsuScoutNew", "Assets", "scoutsu.ico")
SS = 8  # supersampling; geometry is snapped before this, so edges still land on pixels

# Monochrome ramp (light -> dark). Each gradient spans two neighbouring steps only.
RIM = ((226, 80, 150), (201, 52, 120))       # D63F85 family: 3.8:1 on light AND dark
HANDLE = ((210, 62, 130), (184, 42, 106))
GLASS = ((255, 150, 199), (255, 102, 171))   # FF96C7 -> FF66AB (the app accent)
WHITE = (255, 255, 255)

# 48-grid design. Per-size overrides are hand-picked whole-pixel values.
#   lens centre, outer radius, rim width, white ring width, handle end, handle width
DESIGN = dict(cx=19, cy=19, r=15, rim=4, ring=2, hx=42.5, hy=42.5, hw=6)
SMALL = {
    32: dict(cx=13, cy=13, r=10, rim=3, ring=1, hx=28.5, hy=28.5, hw=5),
    24: dict(cx=10, cy=10, r=8, rim=2, ring=1, hx=21, hy=21, hw=4),
    20: dict(cx=8, cy=8, r=7, rim=2, ring=1, hx=17, hy=17, hw=3),
    16: dict(cx=7, cy=7, r=6, rim=2, ring=0, hx=13.5, hy=13.5, hw=3),   # 16px: no white ring - it would be a grey smear
}


def geometry(size):
    if size in SMALL:
        return SMALL[size]
    k = size / 48
    return {key: v * k for key, v in DESIGN.items()}


def grad(n, top_left, bottom_right):
    """120-degree linear gradient across the whole canvas (CSS convention)."""
    ang = math.radians(120)
    dx, dy = math.sin(ang), -math.cos(ang)
    yy, xx = np.mgrid[0:n, 0:n] / n
    t = (xx * dx + yy * dy)
    t = (t - t.min()) / (t.max() - t.min())
    a, b = np.array(top_left, float), np.array(bottom_right, float)
    return a + (b - a) * t[..., None]


def disc_mask(n, cx, cy, r):
    m = Image.new("L", (n, n), 0)
    ImageDraw.Draw(m).ellipse([(cx - r) * SS, (cy - r) * SS, (cx + r) * SS, (cy + r) * SS], fill=255)
    return np.asarray(m, float) / 255


def capsule_mask(n, x1, y1, x2, y2, w):
    m = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(m)
    d.line([(x1 * SS, y1 * SS), (x2 * SS, y2 * SS)], fill=255, width=int(round(w * SS)))
    for x, y in ((x1, y1), (x2, y2)):
        d.ellipse([(x - w / 2) * SS, (y - w / 2) * SS, (x + w / 2) * SS, (y + w / 2) * SS], fill=255)
    return np.asarray(m, float) / 255


def render(size):
    g = geometry(size)
    n = size * SS
    rgb = np.zeros((n, n, 3))
    alpha = np.zeros((n, n))

    def paint(mask, colour):
        nonlocal rgb, alpha
        c = colour if isinstance(colour, np.ndarray) else np.broadcast_to(np.array(colour, float), (n, n, 3))
        rgb = rgb * (1 - mask[..., None]) + c * mask[..., None]
        alpha = alpha + mask * (1 - alpha)

    cx, cy, r = g["cx"], g["cy"], g["r"]
    # Handle starts inside the rim so the joint is hidden under it.
    a = math.radians(45)
    start = (cx + (r - g["rim"]) * math.cos(a), cy + (r - g["rim"]) * math.sin(a))
    handle = capsule_mask(n, start[0], start[1], g["hx"], g["hy"], g["hw"])
    paint(handle, grad(n, *HANDLE))

    rim = disc_mask(n, cx, cy, r)
    if size >= 32:
        # The lens sits on top of the handle: a soft shadow onto the HANDLE ONLY
        # (Microsoft: inner shadows never fall on the background). 1px offset at 48.
        k = size / 48
        sh = Image.fromarray((rim * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2 * k * SS))
        sh = np.roll(np.asarray(sh, float) / 255, int(round(1 * k * SS)), axis=(0, 1))
        shade = sh * handle * (1 - rim) * 0.35
        rgb = rgb * (1 - shade[..., None])
    paint(rim, grad(n, *RIM))

    inner = r - g["rim"]
    if g["ring"]:
        paint(disc_mask(n, cx, cy, inner), WHITE)
        inner -= g["ring"]
    paint(disc_mask(n, cx, cy, inner), grad(n, *GLASS))

    img = np.dstack([rgb, alpha * 255]).clip(0, 255).astype(np.uint8)
    return Image.fromarray(img).resize((size, size), Image.BOX)


SIZES = [256, 64, 48, 40, 32, 24, 20, 16]
renders = {s: render(s) for s in SIZES}

# Multi-size .ico (Microsoft minimum: 16, 24, 32, 48, 256) plus the in-between sizes.
renders[256].save(ICO, sizes=[(s, s) for s in sorted(SIZES)],
                  append_images=[renders[s] for s in sorted(SIZES) if s != 256])
renders[256].save(os.path.join(OUT, "scoutsu_256.png"))

# --- review sheet ---
font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 20)
W, H = 1500, 700
sheet = Image.new("RGB", (W, H), (255, 255, 255))
d = ImageDraw.Draw(sheet)
d.rectangle((0, 0, W // 2, 330), fill=(32, 32, 32))
d.rectangle((W // 2, 0, W, 330), fill=(243, 243, 243))
for x0, fg in ((30, (200, 200, 200)), (W // 2 + 30, (90, 90, 90))):
    x = x0
    for s in SIZES:
        sheet.paste(renders[s], (x, 40 + (256 - s)), renders[s])
        if s <= 64:
            d.text((x, 305), str(s), font=font, fill=fg)
        x += s + 22
# Zoomed small sizes with the pixel grid, to show edges land on whole pixels.
d.text((30, 350), "16 / 24 / 32 px at 8x, with pixel grid (dark and light)", font=font, fill=(60, 60, 60))
x = 30
for bg in ((32, 32, 32), (243, 243, 243)):
    for s in (16, 24, 32):
        z = 8 if s < 32 else 6
        tile = Image.new("RGB", (s, s), bg)
        tile.paste(renders[s], (0, 0), renders[s])
        big = tile.resize((s * z, s * z), Image.NEAREST)
        gd = ImageDraw.Draw(big)
        for i in range(0, s * z, z):
            gd.line([(i, 0), (i, s * z)], fill=(128, 128, 128))
            gd.line([(0, i), (s * z, i)], fill=(128, 128, 128))
        sheet.paste(big, (x, 385))
        x += s * z + 18
sheet.save(os.path.join(OUT, "review.png"))
print("wrote", os.path.normpath(ICO))
