"""Generate the Vigil mark.

The mark is a caret cradling a single dot: the "V" of the name, the prompt
symbol every command line has used for fifty years, and something watching from
inside it. A bare chevron would be any icon; the dot is what makes it this one.

Flat colour, no gradients, no glow - the surface is matte and the only light is
a hairline along the top edge, the way a real object catches a room.

Run:  python tools/make_icon.py
Writes vigil/assets/vigil.ico, vigil.png, logo.svg and mark.svg
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parent.parent / "vigil" / "assets"
SCALE = 8
SIZE = 256
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# Warm, matte, no blue anywhere.
PLATE = (31, 30, 29)          # #1F1E1D
PLATE_EDGE = (255, 255, 255)  # hairline, applied at low alpha
CORAL = (217, 119, 87)        # #D97757


def _caret(draw, canvas: int, colour, weight: float, span: float, top: float, bottom: float):
    """Two strokes meeting at a rounded apex - the caret."""
    centre = canvas / 2
    stroke = round(canvas * weight)
    half = canvas * span

    apex = (centre, canvas * bottom)
    left = (centre - half, canvas * top)
    right = (centre + half, canvas * top)

    draw.line([left, apex], fill=colour, width=stroke, joint="curve")
    draw.line([apex, right], fill=colour, width=stroke, joint="curve")

    # round every end by hand: PIL has no line caps
    for point in (left, apex, right):
        radius = stroke / 2
        draw.ellipse(
            [point[0] - radius, point[1] - radius, point[0] + radius, point[1] + radius],
            fill=colour,
        )


def build_icon(size: int = SIZE) -> Image.Image:
    canvas = size * SCALE
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    radius = int(canvas * 0.225)
    draw.rounded_rectangle([0, 0, canvas - 1, canvas - 1], radius=radius, fill=PLATE + (255,))

    # hairline along the top edge only, so the plate reads as a solid object
    edge = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle(
        [0, 0, canvas - 1, canvas - 1],
        radius=radius,
        outline=PLATE_EDGE + (26,),
        width=max(1, int(canvas * 0.006)),
    )
    image = Image.alpha_composite(image, edge)
    draw = ImageDraw.Draw(image)

    _caret(draw, canvas, CORAL + (255,), weight=0.082, span=0.212, top=0.318, bottom=0.700)

    # the eye, sitting in the opening of the caret rather than under it -
    # below the apex it reads as an upside-down exclamation mark
    dot = canvas * 0.048
    centre = canvas / 2
    baseline = canvas * 0.452
    draw.ellipse([centre - dot, baseline - dot, centre + dot, baseline + dot], fill=CORAL + (255,))

    return image.resize((size, size), Image.LANCZOS)


LOGO_SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <rect width="256" height="256" rx="58" fill="#1F1E1D"/>
  <rect x="1" y="1" width="254" height="254" rx="57" fill="none"
        stroke="#ffffff" stroke-opacity=".10" stroke-width="1.5"/>
  <path d="M73.7 81.4 L128 179.2 L182.3 81.4" fill="none" stroke="#D97757"
        stroke-width="21" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="128" cy="115.7" r="12.3" fill="#D97757"/>
</svg>
"""

# The mark on its own, for use on any background.
MARK_SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <path d="M73.7 81.4 L128 179.2 L182.3 81.4" fill="none" stroke="currentColor"
        stroke-width="21" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="128" cy="115.7" r="12.3" fill="currentColor"/>
</svg>
"""


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    build_icon(SIZE).save(ASSETS / "vigil.png")

    frames = [build_icon(size) for size in ICO_SIZES]
    frames[-1].save(
        ASSETS / "vigil.ico",
        format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
        append_images=frames[:-1],
    )

    (ASSETS / "logo.svg").write_text(LOGO_SVG, encoding="utf-8")
    (ASSETS / "mark.svg").write_text(MARK_SVG, encoding="utf-8")

    for name in ("vigil.ico", "vigil.png", "logo.svg", "mark.svg"):
        print("wrote", ASSETS / name)


if __name__ == "__main__":
    main()
