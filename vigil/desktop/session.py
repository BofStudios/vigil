"""Desktop sessions: one Agent per tab, driving the web front end through events.

The agent was written against a terminal UI, so instead of rewriting it this module
provides a UI adapter with the same surface that turns every call into a JSON event
for the front end. Approvals block the worker thread until the user answers.
"""

from __future__ import annotations

import contextlib
import itertools
import threading
import time
from pathlib import Path

from ..agent import Agent
from ..providers import ProviderError, build_provider
from ..security import Guard
from ..tools import build_registry

_request_ids = itertools.count(1)


class _QuietConsole:
    """Stands in for the rich console the agent touches when streaming is off."""

    @contextlib.contextmanager
    def status(self, *args, **kwargs):
        yield

    def print(self, *args, **kwargs):
        pass

    def clear(self):
        pass


class DesktopUI:
    """Matches the surface `Agent` expects from `ui`, emitting events instead of printing."""

    def __init__(self, emit, tab_id: str):
        self._emit = emit
        self.tab_id = tab_id
        self.quiet = False
        self.interactive = True
        self.console = _QuietConsole()
        self._answers: dict = {}
        self._events: dict = {}
        self._lock = threading.Lock()

    # ---------------------------------------------------------------- events
    def send(self, kind: str, **payload) -> None:
        payload["type"] = kind
        payload["tab"] = self.tab_id
        self._emit(payload)

    # ------------------------------------------------------- assistant text
    def stream_chunk(self, chunk: str) -> None:
        self.send("assistant_chunk", text=chunk)

    def end_stream(self) -> None:
        self.send("assistant_end")

    def assistant(self, text: str) -> None:
        if text and text.strip():
            self.send("assistant_full", text=text)

    # ------------------------------------------------------- tool rendering
    def tool_start(self, name: str, summary: str, risk) -> None:
        self.send("tool", name=name, summary=summary, risk=risk.label)

    def tool_result(self, text: str, ok: bool = True, max_lines: int = 6) -> None:
        self.send("tool_result", text=text or "", ok=bool(ok))

    def plan(self, steps: list) -> None:
        self.send("plan", steps=[dict(step) for step in steps])

    # ------------------------------------------------------------- messages
    def info(self, message: str) -> None:
        self.send("notice", level="info", text=message)

    def dim(self, message: str) -> None:
        self.send("notice", level="dim", text=message)

    def warn(self, message: str) -> None:
        self.send("notice", level="warn", text=message)

    def error(self, message: str) -> None:
        self.send("notice", level="error", text=message)

    def success(self, message: str) -> None:
        self.send("notice", level="success", text=message)

    def rule(self, label: str = "") -> None:
        pass

    def banner(self, *args, **kwargs) -> None:
        pass

    def table(self, title: str, columns: list, rows: list) -> None:
        self.send("table", title=title, columns=list(columns),
                  rows=[[str(cell) for cell in row] for row in rows])

    # ------------------------------------------------------------ approvals
    def confirm(self, action) -> str:
        """Ask the front end and block this worker thread until it answers."""
        request_id = "req-" + str(next(_request_ids))
        event = threading.Event()
        with self._lock:
            self._events[request_id] = event

        self.send(
            "approval",
            request=request_id,
            tool=action.tool,
            summary=action.summary,
            detail=action.detail or "",
            reason=action.verdict.reason or "",
            risk=action.verdict.risk.label,
        )

        event.wait()
        with self._lock:
            answer = self._answers.pop(request_id, "no")
            self._events.pop(request_id, None)
        return answer

    def answer(self, request_id: str, value: str) -> bool:
        with self._lock:
            event = self._events.get(request_id)
            if event is None:
                return False
            self._answers[request_id] = value if value in ("yes", "no", "always") else "no"
            event.set()
        return True

    def release_all(self) -> None:
        """Unblock any pending approval, used when a tab is closed mid-question."""
        with self._lock:
            for request_id, event in list(self._events.items()):
                self._answers[request_id] = "no"
                event.set()


class Session:
    """One tab: its own agent, history, working directory and worker thread."""

    def __init__(self, tab_id: str, config, emit, cwd=None):
        self.id = tab_id
        self.config = config
        self.emit = emit
        self.ui = DesktopUI(emit, tab_id)
        self.title = "New session"
        self.busy = False
        self.error: str = ""
        self._thread = None

        guard = Guard(
            mode=config.approval_mode,
            confirm=self.ui.confirm,
            extra_protected=config.protect_paths,
        )
        registry = build_registry(config)
        provider = build_provider(config)
        self.agent = Agent(config, provider, registry, guard, self.ui, cwd=cwd)
        self.registry = registry

    # ------------------------------------------------------------------ info
    def describe(self) -> dict:
        return {
            "id": self.id,
            "title": self.title,
            "cwd": str(self.agent.ctx.cwd),
            "busy": self.busy,
            "messages": max(0, len(self.agent.messages) - 1),
            "tools": len(self.registry),
        }

    # ------------------------------------------------------------------ work
    def send_message(self, text: str) -> None:
        text = (text or "").strip()
        if not text or self.busy:
            return

        if self.title == "New session":
            self.title = text[:38] + ("..." if len(text) > 38 else "")

        self.busy = True
        self.ui.send("user", text=text)
        self.ui.send("status", busy=True, title=self.title)

        self._thread = threading.Thread(target=self._run, args=(text,), daemon=True)
        self._thread.start()

    def _run(self, text: str) -> None:
        started = time.time()
        try:
            self.agent.run(text)
        except ProviderError as exc:
            self.ui.error(str(exc))
        except Exception as exc:  # never let a worker thread die silently
            self.ui.error(type(exc).__name__ + ": " + str(exc))
        finally:
            self.busy = False
            self.ui.send(
                "status",
                busy=False,
                title=self.title,
                cwd=str(self.agent.ctx.cwd),
                elapsed=round(time.time() - started, 1),
                tokens=getattr(self.agent.provider, "total_tokens", 0),
            )

    def stop(self) -> None:
        self.agent.request_stop()
        self.ui.release_all()

    def set_cwd(self, path: str) -> str:
        target = Path(path).expanduser()
        if not target.is_dir():
            raise ValueError("Not a directory: " + str(target))
        self.agent.ctx.cwd = target.resolve()
        self.agent.refresh_system_prompt()
        return str(self.agent.ctx.cwd)

    def reset(self) -> None:
        self.agent.reset()
        self.agent.ctx.state.pop("plan", None)
        self.title = "New session"

    def close(self) -> None:
        self.stop()
        try:
            self.agent.close()
        except Exception:
            pass
