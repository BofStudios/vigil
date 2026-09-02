"""System tray icon.

Closing the bar hides it rather than quitting, the way a launcher should behave -
the agent may still be working. The tray icon is how you get it back, and the only
place that actually quits.

The icon also carries state. When the bar is hidden it is the only thing on
screen saying whether Vigil is working, or waiting for you to answer something:
a run that has stopped for an approval you cannot see is the worst case, so that
one is drawn loudest.
"""

from __future__ import annotations

import threading
from pathlib import Path

from ..palette import CHROME, MODERATE, SAFE

ICON_PNG = Path(__file__).resolve().parent.parent / "assets" / "vigil.png"

# What the dot on the icon means. Idle has none - a tray icon that always shows
# a badge stops meaning anything.
STATES = {
    "idle": (None, "Vigil"),
    "busy": (SAFE, "Vigil - working"),
    "waiting": (MODERATE, "Vigil - waiting for you"),
}

BADGE = 0.34        # dot size, as a fraction of the icon
BADGE_RING = CHROME  # a hairline so the dot reads on any wallpaper


class Tray:
    """Wraps pystray so the rest of the app never has to care whether it loaded."""

    def __init__(self, on_show=None, on_hide=None, on_quit=None,
                 on_autostart=None, autostart_state=None):
        self.on_show = on_show or (lambda: None)
        self.on_hide = on_hide or (lambda: None)
        self.on_quit = on_quit or (lambda: None)
        self.on_autostart = on_autostart          # None hides the menu item
        self.autostart_state = autostart_state or (lambda: False)
        self.state = "idle"
        self._icon = None
        self._thread = None
        self._base = None
        self._cache: dict = {}
        self.available = False

    def start(self) -> bool:
        """Run the tray icon on its own thread. Returns False if unavailable."""
        try:
            import pystray
            from PIL import Image
        except ImportError:
            return False

        try:
            self._base = Image.open(ICON_PNG).convert("RGBA")
        except Exception:
            return False

        items = [
            pystray.MenuItem("Show Vigil", self._show, default=True),
            pystray.MenuItem("Hide", self._hide),
        ]
        if self.on_autostart is not None:
            items += [
                pystray.Menu.SEPARATOR,
                pystray.MenuItem(
                    "Start with this computer",
                    self._toggle_autostart,
                    checked=lambda _item: bool(self.autostart_state()),
                ),
            ]
        items += [pystray.Menu.SEPARATOR, pystray.MenuItem("Quit", self._quit)]

        self._icon = pystray.Icon("vigil", self._image_for("idle"), "Vigil",
                                  pystray.Menu(*items))
        self._thread = threading.Thread(target=self._icon.run, daemon=True)
        self._thread.start()
        self.available = True
        return True

    # ---------------------------------------------------------------- state
    def set_state(self, state: str) -> None:
        """idle, busy, or waiting. Redrawn only when it actually changes."""
        if state not in STATES or state == self.state:
            return
        self.state = state
        if self._icon is None:
            return
        try:
            self._icon.icon = self._image_for(state)
            self._icon.title = STATES[state][1]
        except Exception:
            pass

    def _image_for(self, state: str):
        """The icon with this state's dot on it, drawn once and kept."""
        if state in self._cache:
            return self._cache[state]

        from PIL import Image, ImageDraw

        colour = STATES[state][0]
        image = self._base.copy() if self._base else Image.new("RGBA", (64, 64))
        if colour is not None:
            size = image.size[0]
            dot = int(size * BADGE)
            left = size - dot - 1
            top = size - dot - 1
            draw = ImageDraw.Draw(image)
            # cleared first, so the dot is the colour it says rather than the
            # colour blended with whatever the mark had underneath
            draw.ellipse([left - 2, top - 2, left + dot + 1, top + dot + 1],
                         fill=(0, 0, 0, 0))
            draw.ellipse([left, top, left + dot, top + dot],
                         fill=colour, outline=BADGE_RING)
        self._cache[state] = image
        return image

    # pystray hands the icon and item to every callback
    def _show(self, icon=None, item=None) -> None:
        self.on_show()

    def _hide(self, icon=None, item=None) -> None:
        self.on_hide()

    def _toggle_autostart(self, icon=None, item=None) -> None:
        if self.on_autostart is None:
            return
        try:
            self.on_autostart(not self.autostart_state())
        except Exception:
            pass
        if self._icon is not None:
            try:
                self._icon.update_menu()
            except Exception:
                pass

    def _quit(self, icon=None, item=None) -> None:
        self.stop()
        self.on_quit()

    def notify(self, message: str, title: str = "Vigil") -> None:
        """Balloon notification - used when the agent finishes while hidden."""
        if self._icon is None:
            return
        try:
            self._icon.notify(message[:200], title)
        except Exception:
            pass

    def stop(self) -> None:
        if self._icon is not None:
            try:
                self._icon.stop()
            except Exception:
                pass
            self._icon = None
        self.available = False
