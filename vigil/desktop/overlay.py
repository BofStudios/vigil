"""Circle something on screen with the mouse, and get back what was under it.

The overlay is a Tk window, which is a good fit: a plain dark scrim that wants
mouse input, and nothing more. The glow in `glow.py` is the opposite - per-pixel
alpha and no input at all - which is why that one is a layered Win32 window.

Tk is imported inside the one function that draws, not at the top, so a machine
without it can still import this module and work out what was circled.
"""

from __future__ import annotations

import platform
import time

IS_WINDOWS = platform.system() == "Windows"
IS_MAC = platform.system() == "Darwin"

SCRIM_ALPHA = 0.35
LASSO_COLOUR = "#ffffff"
LASSO_WIDTH = 3


class Selector:
    """Circle something on screen; get back the box you drew around it."""

    def __init__(self, screen_width: int, screen_height: int):
        self.width = screen_width
        self.height = screen_height
        self.region = None      # (left, top, width, height)
        self.cancelled = False
        self._points: list = []

    def run(self) -> None:
        """Show the overlay and block until the user draws or gives up.

        Tk is imported here rather than at the top: a machine without it can
        still import this module, and everything except the drawing itself -
        `pick`, and working out what was circled - needs no toolkit at all.
        """
        import tkinter as tk

        root = tk.Tk()
        root.withdraw()

        window = tk.Toplevel(root)
        window.overrideredirect(True)
        window.geometry(str(self.width) + "x" + str(self.height) + "+0+0")
        window.attributes("-topmost", True)
        try:
            window.attributes("-alpha", SCRIM_ALPHA)
        except tk.TclError:
            pass
        window.configure(bg="#000000")
        window.config(cursor="crosshair")

        canvas = tk.Canvas(window, width=self.width, height=self.height,
                           bg="#000000", highlightthickness=0)
        canvas.pack()

        label = canvas.create_text(
            self.width // 2, 46,
            text="Draw around what you want to ask about     ·     Esc to cancel",
            fill="#ffffff", font=("Segoe UI", 15),
        )

        drawing = {"on": False}

        def press(event):
            drawing["on"] = True
            self._points = [(event.x_root, event.y_root)]
            canvas.delete(label)

        def drag(event):
            if not drawing["on"]:
                return
            self._points.append((event.x_root, event.y_root))
            if len(self._points) > 1:
                previous, current = self._points[-2], self._points[-1]
                canvas.create_line(previous[0], previous[1], current[0], current[1],
                                   fill=LASSO_COLOUR, width=LASSO_WIDTH,
                                   capstyle=tk.ROUND, smooth=True)

        def release(_event):
            drawing["on"] = False
            self._finish()
            root.quit()

        def cancel(_event=None):
            self.cancelled = True
            root.quit()

        window.bind("<ButtonPress-1>", press)
        window.bind("<B1-Motion>", drag)
        window.bind("<ButtonRelease-1>", release)
        window.bind("<Escape>", cancel)
        window.bind("<Button-3>", cancel)
        window.focus_force()

        try:
            root.mainloop()
        finally:
            try:
                root.destroy()
            except Exception:
                pass
            # let the compositor actually take the overlay away before anyone
            # screenshots what was underneath it
            time.sleep(0.14)

    def _finish(self) -> None:
        if len(self._points) < 3:
            self.cancelled = True
            return

        xs = [point[0] for point in self._points]
        ys = [point[1] for point in self._points]

        # Measured on what was actually drawn, before the padding is added -
        # otherwise the padding alone clears the minimum and a slip of the hand
        # counts as a selection. A swipe along a line of text is short in one
        # direction on purpose, so only something small in both is a stray tap.
        if max(xs) - min(xs) < 12 and max(ys) - min(ys) < 12:
            self.cancelled = True
            return

        pad = 8
        left = max(0, min(xs) - pad)
        top = max(0, min(ys) - pad)
        right = min(self.width, max(xs) + pad)
        bottom = min(self.height, max(ys) + pad)

        if right - left < 12 or bottom - top < 12:
            self.cancelled = True
            return
        self.region = (left, top, right - left, bottom - top)


def pick(timeout: float = 180.0):
    """Ask the user to circle something. Returns (left, top, width, height) or None.

    This runs in a process of its own. Tk insists on the main thread on macOS,
    and in the desktop app that thread belongs to the web view - so rather than
    two GUI toolkits negotiating over one event loop, the overlay gets its own
    process and reports back on stdout.
    """
    import json
    import subprocess
    import sys

    options = {}
    if IS_WINDOWS:
        # started from the tray app there is no console to borrow, and without
        # this a black window blinks up in the middle of the screenshot
        options["creationflags"] = 0x08000000  # CREATE_NO_WINDOW

    try:
        finished = subprocess.run(
            [sys.executable, "-m", "vigil.desktop.overlay"],
            capture_output=True, text=True, timeout=timeout, **options,
        )
    except Exception:
        return None

    for line in reversed(finished.stdout.splitlines()):
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            answer = json.loads(line)
        except ValueError:
            continue
        region = answer.get("region")
        if isinstance(region, list) and len(region) == 4:
            return tuple(int(value) for value in region)
        return None
    return None


def _main() -> int:
    """Entry point for the picker subprocess."""
    import json

    from . import native

    width, height = native.screen_size()
    selector = Selector(width, height)
    try:
        selector.run()
    except Exception as exc:
        print(json.dumps({"error": str(exc)}))
        return 1
    if selector.cancelled or selector.region is None:
        print(json.dumps({"cancelled": True}))
    else:
        print(json.dumps({"region": list(selector.region)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
