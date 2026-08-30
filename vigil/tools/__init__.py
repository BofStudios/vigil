"""Tool registry.

Every tool module publishes a `TOOLS` list. Models see these tools as
OpenAI-compatible function-calling schemas.
"""

from __future__ import annotations

import importlib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from ..security import Guard, Risk


class ToolError(RuntimeError):
    """A tool failure that can be reported back to the model."""


class PermissionDenied(ToolError):
    """The user or the security policy rejected the action."""


@dataclass
class ToolSpec:
    name: str
    description: str
    parameters: dict
    handler: Callable[..., str]
    group: str = "core"
    risk: Risk = Risk.MODERATE
    risk_fn: Optional[Callable[[dict], Risk]] = None
    preview: Optional[Callable[[dict], str]] = None
    quiet_result: bool = False  # the tool draws its own output; skip the echo

    def risk_for(self, args: dict) -> Risk:
        if self.risk_fn is not None:
            try:
                return self.risk_fn(args)
            except Exception:
                return self.risk
        return self.risk

    def summarize(self, args: dict) -> str:
        if self.preview is not None:
            try:
                return self.preview(args)
            except Exception:
                pass
        if not args:
            return self.name
        parts = []
        for key, value in args.items():
            text = str(value)
            if len(text) > 80:
                text = text[:77] + "..."
            parts.append(key + "=" + text)
        return self.name + "(" + ", ".join(parts) + ")"


@dataclass
class ToolContext:
    """Runtime context handed to every tool."""

    config: Any
    guard: Guard
    provider: Any = None
    ui: Any = None
    cwd: Path = field(default_factory=Path.cwd)
    state: dict = field(default_factory=dict)

    def resolve(self, path: str) -> Path:
        """Resolve relative paths against the current working directory."""
        candidate = Path(str(path)).expanduser()
        if not candidate.is_absolute():
            candidate = self.cwd / candidate
        return candidate


class Registry:
    """Registry of loaded tools."""

    def __init__(self) -> None:
        self._tools: dict = {}
        self.skipped: dict = {}
        self.plugins: list = []

    def register(self, spec: ToolSpec) -> None:
        self._tools[spec.name] = spec

    def load_module(self, module_path: str) -> bool:
        """Load a tool module. Skips it and records why when a dependency is missing."""
        try:
            module = importlib.import_module(module_path, package=__package__)
        except Exception as exc:  # pragma: no cover - environment dependent
            self.skipped[module_path] = str(exc)
            return False

        if not getattr(module, "AVAILABLE", True):
            self.skipped[module_path] = getattr(module, "MISSING_HINT", "missing dependency")
            return False

        for spec in getattr(module, "TOOLS", []):
            self.register(spec)
        return True

    def load_plugin_file(self, path: Path) -> int:
        """Load one plugin file and return how many tools it added.

        Plugins are USER CODE and run with full privileges; their tools still pass
        through the Guard, so the risk policy stays in force.
        """
        import importlib.util

        module_name = "vigil_plugin_" + path.stem
        try:
            spec = importlib.util.spec_from_file_location(module_name, path)
            if spec is None or spec.loader is None:
                raise ImportError("could not load module")
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
        except Exception as exc:
            self.skipped["plugin:" + path.name] = str(exc)
            return 0

        tools = getattr(module, "TOOLS", [])
        if not tools:
            self.skipped["plugin:" + path.name] = "no TOOLS list found"
            return 0

        count = 0
        for tool in tools:
            if not isinstance(tool, ToolSpec):
                continue
            if tool.group == "core":
                tool.group = "plugin"
            self.register(tool)
            count += 1
        if count:
            self.plugins.append(path.name)
        return count

    def load_plugins(self, plugin_dir: Path) -> int:
        """Load every plugin in a folder. Files starting with an underscore are skipped."""
        if not plugin_dir.is_dir():
            return 0
        total = 0
        for path in sorted(plugin_dir.glob("*.py")):
            if path.name.startswith("_"):
                continue
            total += self.load_plugin_file(path)
        return total

    def get(self, name: str) -> Optional[ToolSpec]:
        return self._tools.get(name)

    def specs(self) -> list:
        return list(self._tools.values())

    def names(self) -> list:
        return sorted(self._tools)

    def groups(self) -> dict:
        grouped: dict = {}
        for spec in self._tools.values():
            grouped.setdefault(spec.group, []).append(spec)
        for items in grouped.values():
            items.sort(key=lambda s: s.name)
        return grouped

    def __len__(self) -> int:
        return len(self._tools)


def build_registry(config) -> Registry:
    """Load every tool available under the given configuration."""
    registry = Registry()
    registry.load_module(".files")
    registry.load_module(".shell")
    if getattr(config, "enable_planner", True):
        registry.load_module(".planner")
    if getattr(config, "enable_memory", True):
        registry.load_module(".memory_tools")
    if getattr(config, "enable_system", True):
        registry.load_module(".system")
    if getattr(config, "enable_gui", True):
        registry.load_module(".gui")
    if getattr(config, "enable_browser", True):
        registry.load_module(".browser")
    if getattr(config, "enable_plugins", True):
        from ..config import PLUGIN_DIR

        registry.load_plugins(PLUGIN_DIR)
    return registry


def truncate(text: str, limit: int = 12000, note: str = "output truncated") -> str:
    """Shorten long tool output so it does not blow up the model context."""
    if text is None:
        return ""
    text = str(text)
    if len(text) <= limit:
        return text
    head = text[: int(limit * 0.7)]
    tail = text[-int(limit * 0.25) :]
    removed = len(text) - len(head) - len(tail)
    return head + "\n\n... [" + note + ": " + str(removed) + " characters skipped] ...\n\n" + tail


__all__ = [
    "PermissionDenied",
    "Registry",
    "ToolContext",
    "ToolError",
    "ToolSpec",
    "build_registry",
    "truncate",
]
