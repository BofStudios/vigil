"""The Vigil bar: a floating pill at the top of the screen, backed by the agent.

Collapsed it is a single input line. Ask it something and it grows into a panel
with the conversation and the plan; press Escape and it shrinks back. Closing it
hides it to the tray rather than quitting, because a run may still be going.

Front end lives in web/ and talks to this module through `pywebview.api.*`.
Python pushes events the other way with `window.evaluate_js`.
"""

from __future__ import annotations

import itertools
import json
import queue
import sys
import threading
import time
from pathlib import Path

from .. import __version__
from ..config import Config, ensure_dirs
from ..providers import ProviderError, build_provider, provider_notes
from . import native
from .session import Session
from .tray import Tray

WEB_DIR = Path(__file__).resolve().parent / "web"
ASSETS_DIR = Path(__file__).resolve().parent.parent / "assets"
ICON = ASSETS_DIR / "vigil.ico"

WINDOW_TITLE = "Vigil"
BAR_WIDTH = 720
BAR_HEIGHT = 68
PANEL_HEIGHT = 620
TOP_MARGIN = 14
BACKGROUND = "#1F1E1D"  # matches --bg in the stylesheet

_tab_ids = itertools.count(1)


def _js_literal(payload: dict) -> str:
    """JSON that is also safe to paste into a JS expression.

    json.dumps leaves U+2028 and U+2029 raw, and JavaScript treats both as line
    terminators - a stray one inside a tool result would break the injected call.
    """
    text = json.dumps(payload, ensure_ascii=False, default=str)
    return text.replace(chr(0x2028), "\\u2028").replace(chr(0x2029), "\\u2029")


def _ease_out(t: float) -> float:
    return 1 - pow(1 - t, 3)


class Api:
    """Everything the front end can call. Method names are the JS API surface."""

    def __init__(self, config: Config):
        self.config = config
        self.window = None
        self.tray = None
        self.hotkey = None
        self.hwnd = None
        self.sessions: dict = {}
        self.expanded = False
        self.visible = True
        self._outbox: queue.Queue = queue.Queue()
        self._ready = threading.Event()
        self._resizing = threading.Lock()

    # ------------------------------------------------------------ plumbing
    def attach(self, window) -> None:
        self.window = window
        threading.Thread(target=self._drain, daemon=True).start()

    def emit(self, payload: dict) -> None:
        self._outbox.put(payload)

    def _drain(self) -> None:
        self._ready.wait()
        while True:
            payload = self._outbox.get()
            if payload is None:
                return
            try:
                self.window.evaluate_js("window.vigil.receive(" + _js_literal(payload) + ")")
            except Exception:
                pass  # the window may be closing; a dropped UI event is not fatal

    # ------------------------------------------------------------ lifecycle
    def ready(self) -> dict:
        """Called by the front end once it has booted. Returns the initial state."""
        self._ready.set()
        if not self.sessions:
            self.new_tab()
        return self.state()

    def state(self) -> dict:
        return {
            "version": __version__,
            "provider": self.config.provider,
            "model": self.config.active_model,
            "mode": self.config.approval_mode,
            "warning": provider_notes(self.config),
            "hotkey": bool(self.hotkey and self.hotkey.registered),
            "tray": bool(self.tray and self.tray.available),
            "tabs": [session.describe() for session in self.sessions.values()],
        }

    def fit(self) -> dict:
        """Snap the window to the bar size.

        The size passed to create_window is not honoured for a frameless window -
        it comes out at min_size - but resize() sets the viewport exactly, so one
        call after boot puts things right.
        """
        target = PANEL_HEIGHT if self.expanded else BAR_HEIGHT
        try:
            self.window.resize(BAR_WIDTH, target)
        except Exception:
            return {"ok": False}
        return {"ok": True, "height": target}

    # --------------------------------------------------------------- window
    def _animate_height(self, target: int, duration: float = 0.16) -> None:
        """Grow or shrink the window. Stepping it reads as motion rather than a jump."""
        if self.window is None:
            return
        with self._resizing:
            try:
                start = int(self.window.height)
            except Exception:
                start = BAR_HEIGHT
            if start == target:
                return
            steps = 12
            for index in range(1, steps + 1):
                height = round(start + (target - start) * _ease_out(index / steps))
                try:
                    self.window.resize(BAR_WIDTH, height)
                except Exception:
                    return
                time.sleep(duration / steps)

    def expand(self) -> dict:
        if not self.expanded:
            self.expanded = True
            threading.Thread(target=self._animate_height, args=(PANEL_HEIGHT,), daemon=True).start()
        return {"expanded": True}

    def collapse(self) -> dict:
        if self.expanded:
            self.expanded = False
            threading.Thread(target=self._animate_height, args=(BAR_HEIGHT,), daemon=True).start()
        return {"expanded": False}

    def hide_window(self) -> dict:
        """Tuck the bar away. The agent keeps running; the tray brings it back."""
        self.visible = False
        try:
            self.window.hide()
        except Exception:
            pass
        return {"visible": False}

    def show_window(self) -> dict:
        self.visible = True
        try:
            self.window.show()
        except Exception:
            pass
        native.flash_focus(self.hwnd)
        self.emit({"type": "focus", "tab": self._first_tab()})
        return {"visible": True}

    def toggle_window(self) -> None:
        self.hide_window() if self.visible else self.show_window()

    def _first_tab(self) -> str:
        return next(iter(self.sessions), "")

    # ----------------------------------------------------------------- tabs
    def new_tab(self, cwd: str = "") -> dict:
        tab_id = "tab-" + str(next(_tab_ids))
        try:
            session = Session(tab_id, self.config, self.emit, cwd=cwd or None)
        except ProviderError as exc:
            return {"error": str(exc)}
        self.sessions[tab_id] = session
        return session.describe()

    def close_tab(self, tab_id: str) -> dict:
        session = self.sessions.pop(tab_id, None)
        if session is not None:
            session.close()
        if not self.sessions:
            self.new_tab()
        return self.state()

    def reset_tab(self, tab_id: str) -> dict:
        session = self.sessions.get(tab_id)
        if session is None:
            return {"error": "no such tab"}
        session.reset()
        return session.describe()

    # -------------------------------------------------------------- talking
    def send(self, tab_id: str, text: str) -> dict:
        session = self.sessions.get(tab_id)
        if session is None:
            return {"error": "no such tab"}
        if session.busy:
            return {"error": "still working"}
        self.expand()
        session.send_message(text)
        return {"ok": True}

    def stop(self, tab_id: str) -> dict:
        session = self.sessions.get(tab_id)
        if session is not None:
            session.stop()
        return {"ok": True}

    def answer(self, tab_id: str, request_id: str, value: str) -> dict:
        session = self.sessions.get(tab_id)
        if session is None:
            return {"error": "no such tab"}
        return {"ok": session.ui.answer(request_id, value)}

    def notify_done(self, title: str = "") -> None:
        """Ping the tray when a run finishes while the bar is hidden."""
        if not self.visible and self.tray is not None:
            self.tray.notify(title or "Finished.", "Vigil")

    # -------------------------------------------------------------- settings
    def set_mode(self, mode: str) -> dict:
        try:
            self.config.set_value("approval_mode", mode)
        except (KeyError, ValueError) as exc:
            return {"error": str(exc)}
        self.config.save()
        for session in self.sessions.values():
            session.agent.guard.mode = mode
            session.agent.refresh_system_prompt()
        return {"mode": mode}

    def set_model(self, name: str) -> dict:
        self.config.set_active_model(name)
        self.config.save()
        for session in self.sessions.values():
            session.agent.provider.model = name
        return {"model": name, "warning": provider_notes(self.config)}

    def models(self) -> dict:
        try:
            return {"models": build_provider(self.config).list_models()}
        except ProviderError as exc:
            return {"error": str(exc)}

    def tools(self, tab_id: str = "") -> dict:
        session = self.sessions.get(tab_id) or next(iter(self.sessions.values()), None)
        if session is None:
            return {"groups": {}}
        groups: dict = {}
        for group, specs in sorted(session.registry.groups().items()):
            groups[group] = [
                {"name": spec.name, "risk": spec.risk.label, "description": spec.description}
                for spec in specs
            ]
        return {"groups": groups}

    # ------------------------------------------------------------ workspace
    def pick_folder(self, tab_id: str) -> dict:
        session = self.sessions.get(tab_id)
        if session is None or self.window is None:
            return {"error": "no such tab"}
        import webview

        chosen = self.window.create_file_dialog(
            webview.FOLDER_DIALOG, directory=str(session.agent.ctx.cwd)
        )
        if not chosen:
            return {"cancelled": True}
        try:
            return {"cwd": session.set_cwd(chosen[0])}
        except ValueError as exc:
            return {"error": str(exc)}

    # ------------------------------------------------------------- shutdown
    def quit(self) -> None:
        for session in list(self.sessions.values()):
            session.close()
        if self.hotkey is not None:
            self.hotkey.stop()
        if self.tray is not None:
            self.tray.stop()
        try:
            self.window.destroy()
        except Exception:
            pass


def _polish_window(api: Api) -> None:
    """Find the window, apply what the platform offers, then size it correctly.

    The window has to settle before either step: the handle does not exist right
    away, and a resize issued too early is overwritten by the window's own
    initial sizing.
    """
    for _ in range(60):
        hwnd = native.find_window(WINDOW_TITLE)
        if hwnd:
            api.hwnd = hwnd
            native.apply_glass(hwnd, backdrop=native.BACKDROP_ACRYLIC, rounded=True)
            native.set_topmost(hwnd, True)
            time.sleep(0.35)
            api.fit()
            return
        time.sleep(0.05)


def run(config: Config = None, debug: bool = False) -> int:
    """Open the bar. Blocks until it is quit."""
    try:
        import webview
    except ImportError:
        raise SystemExit(
            'The desktop app needs pywebview.\n  pip install "vigil-cli[desktop]"'
        ) from None

    config = config or Config.load()
    ensure_dirs()

    screen_width, _ = native.screen_size()
    api = Api(config)

    options = {
        "js_api": api,
        "width": BAR_WIDTH,
        "height": BAR_HEIGHT,
        # the default minimum is (200, 100), which silently makes the collapsed
        # bar a third taller than it should be
        "min_size": (420, 40),
        "x": max(0, (screen_width - BAR_WIDTH) // 2),
        "y": TOP_MARGIN,
        "frameless": True,
        "easy_drag": False,  # only the grip drags; see .drag-region in the CSS
        "on_top": True,
        "resizable": False,
        "background_color": BACKGROUND,
        "shadow": True,
        "text_select": True,
    }
    if native.IS_MAC:
        # macOS draws the blur itself and rounds the corners for us
        options["vibrancy"] = True
    else:
        options["transparent"] = True

    window = webview.create_window(WINDOW_TITLE, str(WEB_DIR / "index.html"), **options)
    api.attach(window)

    api.tray = Tray(on_show=api.show_window, on_hide=api.hide_window, on_quit=api.quit)
    api.tray.start()

    api.hotkey = native.HotKey(api.toggle_window)
    api.hotkey.start()

    threading.Thread(target=_polish_window, args=(api,), daemon=True).start()

    webview.start(debug=debug, icon=str(ICON) if ICON.exists() else None)

    if api.hotkey is not None:
        api.hotkey.stop()
    if api.tray is not None:
        api.tray.stop()
    return 0


def main() -> int:
    """Entry point for the windowed launcher (`vigil-app`)."""
    return run(debug="--debug" in sys.argv)
