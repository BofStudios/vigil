"""System tray icon.

Closing the bar hides it rather than quitting, the way a launcher should behave -
the agent may still be working. The tray icon is how you get it back, and the only
place that actually quits.
"""

from __future__ import annotations

import threading
from pathlib import Path

ICON_PNG = Path(__file__).resolve().parent.parent / "assets" / "vigil.png"


class Tray:
    """Wraps pystray so the rest of the app never has to care whether it loaded."""

    def __init__(self, on_show=None, on_hide=None, on_quit=None):
        self.on_show = on_show or (lambda: None)
        self.on_hide = on_hide or (lambda: None)
        self.on_quit = on_quit or (lambda: None)
        self._icon = None
        self._thread = None
        self.available = False

    def start(self) -> bool:
        """Run the tray icon on its own thread. Returns False if unavailable."""
        try:
            import pystray
            from PIL import Image
        except ImportError:
            return False

        try:
            image = Image.open(ICON_PNG)
        except Exception:
            return False

        menu = pystray.Menu(
            pystray.MenuItem("Show Vigil", self._show, default=True),
            pystray.MenuItem("Hide", self._hide),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quit", self._quit),
        )
        self._icon = pystray.Icon("vigil", image, "Vigil", menu)

        self._thread = threading.Thread(target=self._icon.run, daemon=True)
        self._thread.start()
        self.available = True
        return True

    # pystray hands the icon and item to every callback
    def _show(self, icon=None, item=None) -> None:
        self.on_show()

    def _hide(self, icon=None, item=None) -> None:
        self.on_hide()

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
