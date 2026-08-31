"""Generate the Vigil app icon.

The mark is a shield split by a vertical gap: the negative space reads as a "V",
the silhouette reads as protection, and a single dot sits where an eye would be -
vigilance. Drawn at 8x and downsampled so the curves stay clean at 16 px.

Run:  python tools/make_icon.py
Writes vigil/assets/vigil.ico, vigil.png and logo.svg
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ASSETS = Path(__file__).resolve().parent.parent / "vigil" / "assets"
SCALE = 8
SIZE = 256
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# Palette - matches the terminal UI accent.
BG_TOP = (14, 18, 26)
BG_BOTTOM = (8, 11, 16)
CYAN = (34, 211, 238)
CYAN_DEEP = (14, 165, 210)
GLOW = (34, 211, 238)


def _vertical_gradient(size: int, top: tuple, bottom: tuple) -> Image.Image:
    gradient = Image.new("RGB", (1, size))
    for y in range(size):
        ratio = y / max(1, size - 1)
        gradient.putpixel(
            (0, y),
            (
                round(top[0] + (bottom[0] - top[0]) * ratio),
                round(top[1] + (bottom[1] - top[1]) * ratio),
                round(top[2] + (bottom[2] - top[2]) * ratio),
            ),
        )
    return gradient.resize((size, size), Image.NEAREST)


def _shield_points(cx: float, top: float, bottom: float, half_width: float) -> list:
    """A shield outline: straight shoulders, tapering to a point."""
    shoulder = top + (bottom - top) * 0.10
    waist = top + (bottom - top) * 0.55
    return [
        (cx - half_width, shoulder),
        (cx - half_width, waist),
        (cx, bottom),
        (cx + half_width, waist),
        (cx + half_width, shoulder),
        (cx, top),
    ]


def build_icon(size: int = SIZE) -> Image.Image:
    canvas = size * SCALE
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))

    # --- rounded background plate with a vertical gradient ---
    plate = _vertical_gradient(canvas, BG_TOP, BG_BOTTOM).convert("RGBA")
    mask = Image.new("L", (canvas, canvas), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, canvas - 1, canvas - 1], radius=int(canvas * 0.22), fill=255
    )
    image.paste(plate, (0, 0), mask)

    # --- soft glow behind the mark ---
    glow = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse(
        [canvas * 0.22, canvas * 0.20, canvas * 0.78, canvas * 0.86],
        fill=GLOW + (70,),
    )
    glow = glow.filter(ImageFilter.GaussianBlur(canvas * 0.09))
    image = Image.alpha_composite(image, glow)

    draw = ImageDraw.Draw(image)

    # --- shield ---
    cx = canvas / 2
    top = canvas * 0.20
    bottom = canvas * 0.83
    half = canvas * 0.235
    stroke = max(1, int(canvas * 0.055))

    # A closed shield reads better than a notched one at 16 px - the "V" already
    # comes from the shield's own point, so the outline stays quiet.
    draw.polygon(_shield_points(cx, top, bottom, half), outline=CYAN, width=stroke)

    # Inner shield line, deeper in tone, for depth.
    inset = canvas * 0.062
    draw.polygon(
        _shield_points(cx, top + inset, bottom - inset * 1.2, half - inset),
        outline=CYAN_DEEP,
        width=max(1, int(stroke * 0.42)),
    )

    # --- the eye: a single dot, centred ---
    dot = canvas * 0.055
    draw.ellipse([cx - dot, canvas * 0.445 - dot, cx + dot, canvas * 0.445 + dot], fill=CYAN)

    return image.resize((size, size), Image.LANCZOS)


SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <defs>
    <linearGradient id="plate" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#0e121a"/>
      <stop offset="100%" stop-color="#080b10"/>
    </linearGradient>
    <filter id="glow" x="-50%" y="-50%" width="200%" height="200%">
      <feGaussianBlur stdDeviation="12" result="b"/>
      <feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
  </defs>
  <rect width="256" height="256" rx="56" fill="url(#plate)"/>
  <g filter="url(#glow)" fill="none" stroke="#22d3ee" stroke-width="14"
     stroke-linejoin="round" stroke-linecap="round">
    <path d="M68 77 V141 L128 212 L188 141 V77 L128 51 Z"/>
  </g>
  <path d="M79 88 V138 L128 196 L177 138 V88 L128 67 Z"
        fill="none" stroke="#0ea5d2" stroke-width="6" stroke-linejoin="round"/>
  <circle cx="128" cy="116" r="13" fill="#22d3ee"/>
</svg>
"""


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    master = build_icon(SIZE)
    master.save(ASSETS / "vigil.png")

    frames = [build_icon(size) for size in ICO_SIZES]
    frames[-1].save(
        ASSETS / "vigil.ico",
        format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
        append_images=frames[:-1],
    )

    (ASSETS / "logo.svg").write_text(SVG, encoding="utf-8")

    print("wrote", ASSETS / "vigil.ico")
    print("wrote", ASSETS / "vigil.png")
    print("wrote", ASSETS / "logo.svg")


if __name__ == "__main__":
    main()
