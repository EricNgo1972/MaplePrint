"""
Generates MaplePrint's application icon.

The constraint that drives every choice here: at 16x16 in the notification area,
next to MapleKiosk's maple leaf, a user must tell them apart instantly. So the
silhouette is a printer, not a leaf — shape carries the distinction, and the
shared brand red carries the family resemblance. Colour alone would not have
been enough; two red blobs at 16px are two red blobs.
"""

import os

from PIL import Image, ImageDraw

MAPLE_RED = (211, 47, 47, 255)
RED_DARK = (154, 28, 28, 255)
PAPER = (252, 252, 252, 255)
# Dark enough to hold the paper's shape against a light taskbar, where a pale
# outline simply disappears and the icon collapses into a red pill.
PAPER_EDGE = (105, 105, 105, 255)

SS = 8           # supersample factor; edges are downscaled to smooth them
BASE = 256

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "..", "MaplePrint", "Images", "mapleprint.ico")


def rr(draw, box, radius, fill, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def render(size_px: int) -> Image.Image:
    s = size_px * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def u(v):
        """Design-grid (0..256) to pixels."""
        return v * s / BASE

    def stroke():
        """
        At least ~1.5 final pixels. Expressing the outline in design units alone
        gives sub-pixel widths at 16px, which vanish entirely in the downscale.
        """
        return max(int(SS * 1.5), int(u(4)))

    # Sheet feeding in at the top, behind the body. Drawn first so the body
    # overlaps it and it reads as "going in".
    rr(d, [u(76), u(26), u(180), u(112)], u(6), PAPER, PAPER_EDGE, stroke())

    # Printer body — the dominant mass, and the thing that reads at 16px.
    rr(d, [u(24), u(92), u(232), u(186)], u(20), MAPLE_RED)

    # Output slot, cut across the body front.
    rr(d, [u(56), u(120), u(200), u(134)], u(7), RED_DARK)

    # Printed sheet emerging at the bottom, in front of everything.
    rr(d, [u(72), u(158), u(184), u(236)], u(6), PAPER, PAPER_EDGE, stroke())

    return img.resize((size_px, size_px), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [render(n) for n in sizes]

# Pillow's ICO writer takes the largest frame plus the size list; passing the
# pre-rendered frames keeps the small ones from being naively downscaled.
frames[-1].save(OUT, format="ICO", sizes=[(n, n) for n in sizes],
                append_images=frames[:-1])

print("wrote", OUT)
for n in sizes:
    print(f"  {n}x{n}")
