"""The Vigil desktop app: a frameless window driven by the same agent as the CLI.

Front end lives in web/ and talks to this module through `pywebview.api.*`.
Python pushes events the other way with `window.evaluate_js`.
"""

from __future__ import annotations

import itertools
import json
import queue
import sys
import threading
from pathlib import Path

from .. import __version__
from ..config import Config, ensure_dirs
from ..providers import ProviderError, build_provider, provider_notes
from .session import Session

WEB_DIR = Path(__file__).resolve().parent / "web"
ASSETS_DIR = Path(__file__).resolve().parent.parent / "assets"
ICON = ASSETS_DIR / "vigil.ico"

WINDOW_WIDTH = 1080
WINDOW_HEIGHT = 720
MIN_SIZE = (760, 520)
BACKGROUND = "#0b0e14"

_tab_ids = itertools.count(1)


def _js_literal(payload: dict) -> str:
    """JSON that is also safe to paste into a JS expression.

    json.dumps leaves U+2028 and U+2029 raw, and JavaScript treats both as line
    terminators - a stray one inside a tool result would break the injected call.
    """
    text = json.dumps(payload, ensure_ascii=False, default=str)
    return text.replace(chr(0x2028), "\\u2028").replace(chr(0x2029), "\\u2029")


class Api:
    """Everything the front end can call. Method names are the JS API surface."""

    def __init__(self, config: Config):
        self.config = config
        self.window = None
        self.sessions: dict = {}
        self._outbox: queue.Queue = queue.Queue()
        self._ready = threading.Event()
        self._pump = None

    # ------------------------------------------------------------ plumbing
    def attach(self, window) -> None:
        self.window = window
        self._pump = threading.Thread(target=self._drain, daemon=True)
        self._pump.start()

    def emit(self, payload: dict) -> None:
        self._outbox.put(payload)

    def _drain(self) -> None:
        """Push events to the front end one at a time, once it says it is ready."""
        self._ready.wait()
        while True:
            payload = self._outbox.get()
            if payload is None:
                return
            try:
                self.window.evaluate_js("window.vigil.receive(" + _js_literal(payload) + ")")
            except Exception:
                # The window may be closing; dropping a UI event is not fatal.
                pass

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
            "tabs": [session.describe() for session in self.sessions.values()],
        }

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

    # --------------------------------------------------------------- window
    def minimize(self) -> None:
        if self.window is not None:
            self.window.minimize()

    def toggle_maximize(self) -> None:
        if self.window is None:
            return
        if getattr(self.window, "_vigil_maximized", False):
            self.window.restore()
            self.window._vigil_maximized = False
        else:
            self.window.maximize()
            self.window._vigil_maximized = True

    def close(self) -> None:
        for session in list(self.sessions.values()):
            session.close()
        if self.window is not None:
            self.window.destroy()


def run(config: Config = None, debug: bool = False) -> int:
    """Open the desktop window. Blocks until it is closed."""
    try:
        import webview
    except ImportError:
        raise SystemExit(
            "The desktop app needs pywebview.\n"
            '  pip install "vigil-cli[desktop]"'
        ) from None

    config = config or Config.load()
    ensure_dirs()

    api = Api(config)
    window = webview.create_window(
        "Vigil",
        str(WEB_DIR / "index.html"),
        js_api=api,
        width=WINDOW_WIDTH,
        height=WINDOW_HEIGHT,
        min_size=MIN_SIZE,
        frameless=True,
        easy_drag=False,  # only the title bar drags; see .drag-region in the CSS
        background_color=BACKGROUND,
        text_select=True,
    )
    api.attach(window)

    webview.start(debug=debug, icon=str(ICON) if ICON.exists() else None)
    return 0


def main() -> int:
    """Entry point for the windowed launcher (`vigil-app`)."""
    return run(debug="--debug" in sys.argv)
