"""One palette, shared by the bar and the terminal.

These are the same values as :root in vigil/desktop/web/style.css, so the two
faces of the app cannot drift apart. Colour is reserved for meaning: chrome for
the product itself, and three signals for how risky an action is.

Nothing here is blue. The whole look is warm and matte, and a cold accent in the
terminal was the one place that still broke it.
"""

CHROME = "#ececef"      # the product's own colour: light on near-black
DIM = "#8a8a90"         # supporting text and rules
SAFE = "#7fb289"
MODERATE = "#cfae6a"
HIGH = "#d6786f"
