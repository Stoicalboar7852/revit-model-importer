"""Draws the installer's icon and wizard bitmaps.

The add-in draws its ribbon icons in code (IconFactory.cs); NSIS needs files, so this script draws the same
cube-and-arrow design into packaging/installer/art/. The output is committed; run this again only to change the art:

    python3 packaging/installer/make-art.py

Needs Pillow (build time only; not shipped).
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ART = Path(__file__).resolve().parent / "art"
SS = 4  # supersampling factor

# Colours from IconFactory.DrawImport.
TOP = (0x5B, 0x9B, 0xD5)
LEFT = (0x2E, 0x75, 0xB6)
RIGHT = (0x1F, 0x4E, 0x79)
OUTLINE = (0x10, 0x2A, 0x43)
ARROW = (0x37, 0xA1, 0x3C)
ARROW_OUTLINE = (0x1E, 0x5E, 0x22)


def draw_logo(draw: ImageDraw.ImageDraw, x0: float, y0: float, s: float, outline_width: float) -> None:
    """The ribbon icon: an isometric cube with an arrow pointing into it, in a box at (x0, y0) of size s."""

    def p(x: float, y: float) -> tuple[float, float]:
        return (x0 + x * s, y0 + y * s)

    w = max(1, round(outline_width))
    faces = [
        (TOP, [p(0.50, 0.30), p(0.88, 0.48), p(0.50, 0.66), p(0.12, 0.48)]),
        (LEFT, [p(0.12, 0.48), p(0.50, 0.66), p(0.50, 0.98), p(0.12, 0.80)]),
        (RIGHT, [p(0.50, 0.66), p(0.88, 0.48), p(0.88, 0.80), p(0.50, 0.98)]),
    ]
    for fill, points in faces:
        draw.polygon(points, fill=fill, outline=OUTLINE, width=w)
    arrow = [p(0.42, 0.02), p(0.58, 0.02), p(0.58, 0.20), p(0.70, 0.20), p(0.50, 0.42), p(0.30, 0.20), p(0.42, 0.20)]
    draw.polygon(arrow, fill=ARROW, outline=ARROW_OUTLINE, width=w)


def icon_image(size: int) -> Image.Image:
    big = Image.new("RGBA", (size * SS, size * SS), (0, 0, 0, 0))
    # The ribbon's 1/32 outline, thinner at large sizes where it would look heavy.
    draw_logo(ImageDraw.Draw(big), 0, 0, size * SS, max(1, size / 32 if size <= 48 else size / 56) * SS)
    return big.resize((size, size), Image.LANCZOS)


def write_icon() -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    images = [icon_image(s) for s in sizes]
    # DIB entries rather than PNG: every NSIS version accepts them.
    images[-1].save(ART / "installer.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1], bitmap_format="bmp")


def lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def write_wizard() -> None:
    """Welcome/finish side panel, 164x314 (MUI2's size at 96 DPI)."""
    w, h = 164, 314
    W, H = w * SS, h * SS
    top, bottom = (0x1B, 0x4F, 0x80), (0x0A, 0x1F, 0x36)
    img = Image.new("RGB", (W, H))
    px = img.load()
    for y in range(H):
        for x in range(W):
            t = min(1.0, max(0.0, 0.85 * y / H + 0.15 * x / W))
            px[x, y] = lerp(top, bottom, t)

    # A faint isometric grid, fading out towards the bottom.
    grid = Image.new("L", (W, H), 0)
    g = ImageDraw.Draw(grid)
    step = 22 * SS
    for k in range(-H // step - 2, (W + H) // step + 2):
        c = k * step
        g.line([(c, 0), (c - H * 2, H)], fill=26, width=SS)  # rising lines (slope 1/2)
        g.line([(c - H * 2, 0), (c, H)], fill=26, width=SS)  # falling lines
    fade = Image.linear_gradient("L").resize((W, H)).point(lambda v: 255 - v)
    grid = Image.composite(grid, Image.new("L", (W, H), 0), fade)
    img = Image.composite(Image.new("RGB", (W, H), (0x9C, 0xC8, 0xF0)), img, grid)

    # Soft glow behind the logo.
    glow = Image.new("L", (W, H), 0)
    cx, cy, r = W // 2, int(H * 0.36), int(W * 0.42)
    ImageDraw.Draw(glow).ellipse([cx - r, cy - r, cx + r, cy + r], fill=70)
    glow = glow.filter(ImageFilter.GaussianBlur(18 * SS))
    img = Image.composite(Image.new("RGB", (W, H), (0x6F, 0xB3, 0xF0)), img, glow)

    logo = W * 0.62
    draw_logo(ImageDraw.Draw(img), (W - logo) / 2, cy - logo * 0.52, logo, 2 * SS)

    # A thin accent line near the bottom.
    ImageDraw.Draw(img).rectangle([int(W * 0.18), int(H * 0.80), int(W * 0.82), int(H * 0.80) + SS], fill=(0x37, 0xA1, 0x3C))
    img.resize((w, h), Image.LANCZOS).save(ART / "wizard.bmp")


def write_header() -> None:
    """Header image on the inner pages, 150x57, white like the header background; logo on the right."""
    w, h = 150, 57
    img = Image.new("RGB", (w * SS, h * SS), (255, 255, 255))
    s = 44 * SS
    draw_logo(ImageDraw.Draw(img), (w - 6) * SS - s, (h * SS - s) / 2, s, 1.5 * SS)
    img.resize((w, h), Image.LANCZOS).save(ART / "header.bmp")


if __name__ == "__main__":
    ART.mkdir(exist_ok=True)
    write_icon()
    write_wizard()
    write_header()
    print(f"Wrote {', '.join(sorted(p.name for p in ART.iterdir()))} to {ART}")
