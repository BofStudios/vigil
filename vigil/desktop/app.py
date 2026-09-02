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
import os
import queue
import sys
import threading
import time
from pathlib import Path

from .. import __version__, brains
from ..config import Config, ensure_dirs
from ..providers import ProviderError, build_provider, provider_notes
from . import native
from .glow import light as control_light
from .session import Session
from .tray import Tray
from .voice import PushToTalk, Recorder
from .voice import available as voice_available

WEB_DIR = Path(__file__).resolve().parent / "web"
ASSETS_DIR = Path(__file__).resolve().parent.parent / "assets"
ICON = ASSETS_DIR / "vigil.ico"

WINDOW_TITLE = "Vigil"
# Three shapes: a pill that sits out of the way, the bar you type into, and
# the panel the work appears in.
PILL_WIDTH = 208
PILL_HEIGHT = 46
BAR_WIDTH = 720
BAR_HEIGHT = 68
PANEL_HEIGHT = 620
TOP_MARGIN = 14

# How close the pointer has to get before the pill opens, and how far it has
# to leave before it folds again. The gap between the two stops the bar
# flickering when the pointer rests on the boundary.
REACH_IN = 8
REACH_OUT = 90
WATCH_INTERVAL = 0.06

# set VIGIL_DEBUG_POINTER=1 to watch the hover logic decide
_POINTER_DEBUG = bool(os.environ.get("VIGIL_DEBUG_POINTER"))
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
        self.ask_hotkey = None
        self.asking = False
        self.talk = None
        self.recorder = None
        self.listening = False
        self.hwnd = None
        self.sessions: dict = {}
        self.expanded = False
        self.resting = True
        # set by the front end while there is text in the box, so the bar
        # does not fold away over something half-typed
        self.holding = False
        self.visible = True
        self.screen_width = native.screen_size()[0]
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
            "brain": self.config.brain,
            "brains": brains.describe_all(),
            "warning": provider_notes(self.config),
            "hotkey": bool(self.hotkey and self.hotkey.registered),
            "tray": bool(self.tray and self.tray.available),
            "voice": bool(self.talk and self.talk.listening),
            "voice_key": self.config.voice_key,
            "ask_screen": bool(self.ask_hotkey and self.ask_hotkey.registered),
            "tabs": [session.describe() for session in self.sessions.values()],
        }

    def fit(self) -> dict:
        """Snap the window to the bar size.

        The size passed to create_window is not honoured for a frameless window -
        it comes out at min_size - but resize() sets the viewport exactly, so one
        call after boot puts things right.
        """
        if self.expanded:
            width, height = BAR_WIDTH, PANEL_HEIGHT
        elif self.resting:
            width, height = PILL_WIDTH, PILL_HEIGHT
        else:
            width, height = BAR_WIDTH, BAR_HEIGHT
        try:
            self.window.resize(width, height)
            self.window.move(max(0, (self.screen_width - width) // 2), TOP_MARGIN)
        except Exception:
            return {"ok": False}
        return {"ok": True, "width": width, "height": height}

    # --------------------------------------------------------------- window
    def _animate_to(self, width: int, height: int, duration: float = 0.2) -> None:
        """Grow or shrink the window. Stepping it reads as motion rather than a jump.

        The window stays centred horizontally while it changes width, otherwise
        it would appear to slide sideways as it grows.
        """
        if self.window is None:
            return
        with self._resizing:
            try:
                start_w = int(self.window.width)
                start_h = int(self.window.height)
            except Exception:
                start_w, start_h = BAR_WIDTH, BAR_HEIGHT
            if (start_w, start_h) == (width, height):
                return

            steps = 14
            for index in range(1, steps + 1):
                progress = _ease_out(index / steps)
                current_w = round(start_w + (width - start_w) * progress)
                current_h = round(start_h + (height - start_h) * progress)
                try:
                    self.window.resize(current_w, current_h)
                    if current_w != start_w:
                        self.window.move(max(0, (self.screen_width - current_w) // 2), TOP_MARGIN)
                except Exception:
                    return
                time.sleep(duration / steps)

    def _shape(self, width: int, height: int) -> None:
        threading.Thread(target=self._animate_to, args=(width, height), daemon=True).start()

    def rest(self) -> dict:
        """Shrink back to the pill - only when there is nothing going on."""
        if self.expanded or self.resting:
            return {"resting": self.resting}
        self.resting = True
        self._shape(PILL_WIDTH, PILL_HEIGHT)
        self.emit({"type": "shape", "tab": self._first_tab(), "resting": True})
        return {"resting": True}

    def peek(self) -> dict:
        """Open out to the full bar, for a pointer arriving or a hot key."""
        if self.expanded or not self.resting:
            return {"resting": False}
        self.resting = False
        self._shape(BAR_WIDTH, BAR_HEIGHT)
        self.emit({"type": "shape", "tab": self._first_tab(), "resting": False})
        return {"resting": False}

    def expand(self) -> dict:
        if not self.expanded:
            self.expanded = True
            self.resting = False
            self._shape(BAR_WIDTH, PANEL_HEIGHT)
        return {"expanded": True}

    def collapse(self) -> dict:
        if self.expanded:
            self.expanded = False
            self._shape(BAR_WIDTH, BAR_HEIGHT)
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
        if self.visible and not self.resting:
            self.hide_window()
            return
        self.show_window()
        self.peek()

    def busy_anywhere(self) -> bool:
        return any(session.busy for session in self.sessions.values())

    def hold(self, holding: bool) -> dict:
        """The front end tells us when the box has something in it."""
        self.holding = bool(holding)
        return {"holding": self.holding}

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
        session = self.sessions.get(tab_id) or next(iter(self.sessions.values()), None)
        if session is None:
            return {"error": "still starting up - try again in a moment"}
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

    # ----------------------------------------------------------------- voice
    def start_listening(self) -> None:
        """The talk key went down."""
        if self.recorder is None or self.listening:
            return
        try:
            self.recorder.start()
        except Exception as exc:
            self.emit({"type": "voice", "tab": self._first_tab(),
                       "state": "error", "text": str(exc)})
            return

        self.listening = True
        self.show_window()
        self.peek()
        self.emit({"type": "voice", "tab": self._first_tab(), "state": "listening"})

    def stop_listening(self) -> None:
        """The talk key came up: transcribe on a worker so the key stays responsive."""
        if self.recorder is None or not self.listening:
            return
        self.listening = False
        audio = self.recorder.stop()

        if not audio:
            self.emit({"type": "voice", "tab": self._first_tab(), "state": "idle"})
            return

        self.emit({"type": "voice", "tab": self._first_tab(), "state": "thinking"})
        threading.Thread(target=self._transcribe, args=(audio,), daemon=True).start()

    def _transcribe(self, audio: bytes) -> None:
        tab = self._first_tab()
        session = self.sessions.get(tab)
        provider = session.agent.provider if session else None
        if provider is None:
            self.emit({"type": "voice", "tab": tab, "state": "error", "text": "no provider"})
            return
        try:
            text = provider.transcribe(audio, language=self.config.voice_language)
        except Exception as exc:
            self.emit({"type": "voice", "tab": tab, "state": "error", "text": str(exc)})
            return

        text = (text or "").strip()
        # Whisper answers a silent clip with a lone full stop
        if not text or text in (".", "...", "Thank you."):
            self.emit({"type": "voice", "tab": tab, "state": "idle"})
            return
        self.emit({"type": "voice", "tab": tab, "state": "text", "text": text})

    # ------------------------------------------------------------ ask screen
    def ask_screen(self) -> dict:
        """Circle something on screen, and Vigil explains what it is.

        The picker blocks until the user draws or gives up, so it runs on a
        worker; the hot key thread has a message loop to get back to.
        """
        if self.asking:
            return {"ok": False, "error": "already picking"}
        self.asking = True
        threading.Thread(target=self._ask_screen, daemon=True).start()
        return {"ok": True}

    def _ask_screen(self) -> None:
        tab = self._first_tab()
        try:
            from .overlay import pick

            region = pick()
            if region is None:
                self.emit({"type": "voice", "tab": tab, "state": "idle"})
                return

            self.emit({"type": "voice", "tab": tab, "state": "thinking",
                       "label": "Looking"})
            description = self._describe(region)
        except Exception as exc:
            self.emit({"type": "voice", "tab": tab, "state": "error", "text": str(exc)})
            return
        finally:
            self.asking = False

        if not description:
            return

        # The picture becomes words here, and the agent takes it from there - so
        # it can search, open a page or look something up, the same as it would
        # for anything else typed into the bar.
        self.emit({"type": "voice", "tab": tab, "state": "idle"})
        self.show_window()
        self.send(tab, (
            "I circled part of my screen. It shows: " + description + "\n\n"
            "Tell me what this is and what I should know about it. "
            "Look it up on the web if that would make the answer better."
        ))

    def _describe(self, region) -> str:
        """Turn the circled pixels into a description the model can work from."""
        session = self.sessions.get(self._first_tab())
        provider = session.agent.provider if session else None
        if provider is None:
            raise RuntimeError("no provider")

        from ..tools import gui

        if not gui.AVAILABLE:
            raise RuntimeError(gui.MISSING_HINT or "screen capture is unavailable")

        image, _area = gui._capture(1, region)
        return provider.vision(
            "Describe what is in this image in detail: the text it contains, "
            "what application or page it appears to be from, and anything "
            "identifiable in it. Be specific and factual.",
            gui._encode(image),
        ).strip()

    # -------------------------------------------------------------- settings
    def set_brain(self, key: str) -> dict:
        """Switch how Vigil thinks.

        This is a model choice as well as a prompt one - the two ways of working
        run on different models - so the live provider is repointed too, and not
        only the saved setting.
        """
        try:
            self.config.set_value("brain", key)
        except (KeyError, ValueError) as exc:
            return {"error": str(exc)}

        brain = brains.get(key)
        # Ollama users have their own model pulled locally; only the hosted
        # provider has both of these to choose between.
        repoint = self.config.provider == "groq" and bool(brain.model)
        if repoint:
            self.config.set_active_model(brain.model)
        self.config.save()

        for session in self.sessions.values():
            session.agent.set_brain(key)
            if repoint:
                try:
                    session.agent.provider.model = brain.model
                except Exception:
                    pass
        return {"brain": key, "model": self.config.active_model}

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
        if self.talk is not None:
            self.talk.stop()
        if self.recorder is not None:
            self.recorder.cancel()
        if self.hotkey is not None:
            self.hotkey.stop()
        if self.ask_hotkey is not None:
            self.ask_hotkey.stop()
        if self.tray is not None:
            self.tray.stop()
        control_light().stop()
        try:
            self.window.destroy()
        except Exception:
            pass


def _watch_pointer(api: Api) -> None:
    """Open the pill when the pointer arrives, fold it when the pointer leaves.

    The web layer's own mouseenter never fired reliably for this window, and
    polling the cursor is both dependable and closer to what we want anyway:
    the bar can react to the pointer approaching, not just landing on it.
    """
    while True:
        time.sleep(WATCH_INTERVAL)
        if not api.visible or api.expanded or api.window is None:
            continue

        point = native.cursor_position()
        rect = native.window_rect(api.hwnd)
        if rect is None:
            # the handle went stale - find the window again rather than
            # silently giving up on hover for the rest of the session
            api.hwnd = native.find_window(WINDOW_TITLE)
            rect = native.window_rect(api.hwnd)
        if point is None or rect is None:
            continue

        left, top, right, bottom = rect
        x, y = point

        if _POINTER_DEBUG:
            print("pointer", (x, y), "rect", rect, "resting", api.resting, flush=True)

        if api.resting:
            near = (left - REACH_IN <= x <= right + REACH_IN
                    and top - REACH_IN <= y <= bottom + REACH_IN)
            if near:
                api.peek()
        else:
            gone = not (left - REACH_OUT <= x <= right + REACH_OUT
                        and top - REACH_OUT <= y <= bottom + REACH_OUT)
            if gone and not api.holding and not api.busy_anywhere():
                api.rest()


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

    # Loading the tool registry pulls in Playwright and friends, which takes a
    # few seconds. Doing it here rather than on the front end's first call means
    # the bar is usable the moment it appears instead of silently swallowing
    # whatever gets typed into it first.
    api.new_tab()

    options = {
        "js_api": api,
        "width": PILL_WIDTH,
        "height": PILL_HEIGHT,
        # the default minimum is (200, 100), which silently makes the collapsed
        # bar a third taller than it should be
        "min_size": (180, 40),
        "x": max(0, (screen_width - PILL_WIDTH) // 2),
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
    # No transparency on Windows: the surface is opaque now, and a transparent
    # WebView2 window swallows pointer events, which stopped hover from working.

    window = webview.create_window(WINDOW_TITLE, str(WEB_DIR / "index.html"), **options)
    api.attach(window)

    api.tray = Tray(on_show=api.show_window, on_hide=api.hide_window, on_quit=api.quit)
    api.tray.start()

    api.hotkey = native.HotKey(api.toggle_window)
    api.hotkey.start()

    # Build the screen glow now, in the background: the first time Vigil reaches
    # for the mouse it should light up at once, not half a second later.
    if config.enable_gui:
        control_light().prepare()

    if config.enable_gui and config.enable_ask_screen:
        api.ask_hotkey = native.HotKey(
            api.ask_screen, native.MOD_CONTROL | native.MOD_SHIFT, native.VK_A,
            combo="<ctrl>+<shift>+a",
        )
        if not api.ask_hotkey.start():
            api.ask_hotkey = None
            print("vigil: ctrl+shift+A is already taken - circling a region is off")

    if config.enable_voice:
        ok, reason = voice_available()
        if ok:
            api.recorder = Recorder()
            api.talk = PushToTalk(config.voice_key, api.start_listening, api.stop_listening)
            started, why = api.talk.start()
            if not started:
                api.talk = None
                print("vigil: push to talk is off -", why)
        else:
            print("vigil: push to talk is off -", reason)

    threading.Thread(target=_polish_window, args=(api,), daemon=True).start()
    threading.Thread(target=_watch_pointer, args=(api,), daemon=True).start()

    webview.start(debug=debug, icon=str(ICON) if ICON.exists() else None)

    if api.talk is not None:
        api.talk.stop()
    if api.hotkey is not None:
        api.hotkey.stop()
    if api.tray is not None:
        api.tray.stop()
    return 0


def main() -> int:
    """Entry point for the windowed launcher (`vigil-app`)."""
    return run(debug="--debug" in sys.argv)
