"""Generate the Vigil mark.

One stroke that reads two ways: the "V" of the name, and a tick. The right arm
rises past where a symmetrical V would stop, which is the whole idea - this is
an agent that does the thing and then reports it done. The asymmetry is what
makes it this mark rather than any chevron.

It replaces a symmetrical caret with a dot in its mouth. That dot was doing the
work of making the mark distinctive, and at sixteen pixels it stopped being a
dot and started being a smudge.

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

# Near-black plate, chrome mark. No colour anywhere: the light in this mark
# is a highlight, not a hue.
PLATE = (6, 6, 7)             # #060607
PLATE_EDGE = (255, 255, 255)  # hairline, applied at low alpha
CHROME = (236, 236, 239)      # #ECECEF


#: the mark, on a 256 grid. The right arm ends higher than the left starts,
#: which is what turns a V into a tick without it stopping being a V.
LEFT = (68, 76)
VERTEX = (124, 178)
RIGHT = (188, 58)
STROKE = 22


def _mark(draw, canvas: int, colour):
    """The V, drawn at whatever size the canvas is."""
    unit = canvas / 256.0
    stroke = max(1, round(STROKE * unit))
    points = [(x * unit, y * unit) for x, y in (LEFT, VERTEX, RIGHT)]

    draw.line(points, fill=colour, width=stroke, joint="curve")

    # round every end by hand: PIL has no line caps
    radius = stroke / 2
    for x, y in points:
        draw.ellipse([x - radius, y - radius, x + radius, y + radius], fill=colour)


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
        outline=PLATE_EDGE + (46,),
        width=max(1, int(canvas * 0.006)),
    )
    image = Image.alpha_composite(image, edge)
    draw = ImageDraw.Draw(image)

    _mark(draw, canvas, CHROME + (255,))

    return image.resize((size, size), Image.LANCZOS)


LOGO_SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <rect width="256" height="256" rx="58" fill="#060607"/>
  <rect x="1" y="1" width="254" height="254" rx="57" fill="none"
        stroke="#ffffff" stroke-opacity=".18" stroke-width="1.5"/>
  <path d="M68 76 L124 178 L188 58" fill="none" stroke="#ECECEF"
        stroke-width="22" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
"""

# The mark on its own, for use on any background.
MARK_SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <path d="M68 76 L124 178 L188 58" fill="none" stroke="currentColor"
        stroke-width="22" stroke-linecap="round" stroke-linejoin="round"/>
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
