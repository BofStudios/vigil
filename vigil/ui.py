"""Terminal interface: banner, tool call rendering, approval dialogs."""

from __future__ import annotations

import sys

from rich.console import Console, Group
from rich.markdown import Markdown
from rich.panel import Panel
from rich.syntax import Syntax
from rich.table import Table
from rich.text import Text

from . import __version__
from .security import Action, Risk

BANNER = r"""
 __     _____ ____ ___ _
 \ \   / /_ _/ ___|_ _| |
  \ \ / / | | |  _ | || |
   \ V /  | | |_| || || |___
    \_/  |___\____|___|_____|
"""

RISK_ICON = {Risk.SAFE: "*", Risk.MODERATE: "!", Risk.HIGH: "!!", Risk.BLOCKED: "X"}


class UI:
    """Vigil terminal output. With interactive=False nothing is ever asked."""

    def __init__(self, interactive: bool = True, quiet: bool = False):
        self.console = Console(highlight=False, soft_wrap=True)
        self.interactive = interactive and sys.stdin.isatty()
        self.quiet = quiet
        self._streaming = False

    # ------------------------------------------------------------ general
    def banner(self, model: str, mode: str, tool_count: int, cwd: str) -> None:
        if self.quiet:
            return
        self.console.print(Text(BANNER, style="bold cyan"))
        info = Table.grid(padding=(0, 2))
        info.add_column(style="dim")
        info.add_column()
        info.add_row("version", "v" + __version__ + "  ·  BOF Studios")
        info.add_row("model", model)
        info.add_row("mode", _mode_label(mode))
        info.add_row("tools", str(tool_count) + " ready")
        info.add_row("directory", cwd)
        self.console.print(info)
        self.console.print(
            Text("  /help lists the commands  ·  /exit or Ctrl+C to quit", style="dim italic")
        )
        self.console.print()

    def info(self, message: str) -> None:
        if not self.quiet:
            self.console.print(Text(message, style="cyan"))

    def dim(self, message: str) -> None:
        if not self.quiet:
            self.console.print(Text(message, style="dim"))

    def warn(self, message: str) -> None:
        self.console.print(Text("warning: " + message, style="yellow"))

    def error(self, message: str) -> None:
        self.console.print(Text("error: " + message, style="bold red"))

    def success(self, message: str) -> None:
        if not self.quiet:
            self.console.print(Text(message, style="green"))

    def rule(self, label: str = "") -> None:
        if not self.quiet:
            self.console.rule(label, style="dim")

    # -------------------------------------------------------- assistant text
    def stream_chunk(self, chunk: str) -> None:
        if not self._streaming:
            self.console.print(Text("vigil", style="bold magenta"))
            self._streaming = True
        self.console.file.write(chunk)
        self.console.file.flush()

    def end_stream(self) -> None:
        if self._streaming:
            self.console.file.write("\n")
            self.console.file.flush()
            self._streaming = False

    def assistant(self, text: str) -> None:
        """Render the full answer as markdown when streaming is off."""
        if not text.strip():
            return
        self.console.print(Text("vigil", style="bold magenta"))
        try:
            self.console.print(Markdown(text))
        except Exception:
            self.console.print(text)
        self.console.print()

    # ------------------------------------------------------- tool rendering
    def tool_start(self, name: str, summary: str, risk: Risk) -> None:
        if self.quiet:
            return
        line = Text()
        line.append("  " + RISK_ICON.get(risk, "*") + " ", style=risk.color)
        line.append(name, style="bold")
        if summary and summary != name:
            line.append("  " + _one_line(summary, 90), style="dim")
        self.console.print(line)

    def tool_result(self, text: str, ok: bool = True, max_lines: int = 6) -> None:
        if self.quiet:
            return
        lines = (text or "").strip().splitlines()
        style = "dim" if ok else "red"
        for line in lines[:max_lines]:
            self.console.print(Text("     " + _one_line(line, 110), style=style))
        if len(lines) > max_lines:
            self.console.print(Text("     ... (" + str(len(lines) - max_lines) + " more lines)", style="dim"))

    # --------------------------------------------------------------- planning
    PLAN_STYLE = {"todo": "dim", "doing": "bold cyan", "done": "green", "blocked": "red"}

    def plan(self, steps: list) -> None:
        """Render the task checklist so the user can follow along."""
        if self.quiet or not steps:
            return
        body = Text()
        for index, step in enumerate(steps, 1):
            status = step.get("status", "todo")
            icon = {"todo": "[ ]", "doing": "[>]", "done": "[x]", "blocked": "[!]"}.get(status, "[ ]")
            style = self.PLAN_STYLE.get(status, "dim")
            body.append("  " + icon + " ", style=style)
            body.append(step.get("text", ""), style=style if status != "done" else "dim")
            if step.get("note"):
                body.append("  " + _one_line(step["note"], 30), style="dim italic")
            if index < len(steps):
                body.append("\n")
        done = sum(1 for step in steps if step.get("status") == "done")
        title = "plan · " + str(done) + "/" + str(len(steps)) + " done"
        self.console.print(Panel(body, title=title, border_style="cyan", padding=(0, 1)))

    # ------------------------------------------------------------ approvals
    def confirm(self, action: Action) -> str:
        """Ask the user for approval. Returns: yes | no | always"""
        if not self.interactive:
            return "no"

        risk = action.verdict.risk
        body = [Text(action.summary, style="bold white")]
        if action.verdict.reason:
            body.append(Text("reason: " + action.verdict.reason, style="dim"))
        if action.detail:
            body.append(Text(""))
            body.append(_detail_renderable(action.detail))

        title = "approval required · " + action.tool + " · risk: " + risk.label
        self.console.print(Panel(Group(*body), title=title, border_style=risk.color, padding=(0, 1)))

        prompt = Text()
        prompt.append("  [y]", style="bold green")
        prompt.append("es  ")
        prompt.append("[n]", style="bold red")
        prompt.append("o  ")
        prompt.append("[a]", style="bold yellow")
        prompt.append("lways allow (this session)  > ")

        while True:
            self.console.print(prompt, end="")
            try:
                answer = input().strip().lower()
            except (EOFError, KeyboardInterrupt):
                self.console.print()
                return "no"
            if answer in ("y", "yes", ""):
                return "yes"
            if answer in ("n", "no"):
                return "no"
            if answer in ("a", "all", "always"):
                if risk is Risk.HIGH:
                    self.warn("high-risk actions cannot be pre-approved; applied once only.")
                    return "yes"
                return "always"
            self.console.print(Text("  expecting y / n / a.", style="dim"))

    def blocked(self, action: Action, reason: str) -> None:
        self.console.print(
            Panel(
                Text(action.summary + "\n\n" + reason, style="bright_red"),
                title="blocked · security policy",
                border_style="bright_red",
                padding=(0, 1),
            )
        )

    # --------------------------------------------------------------- tables
    def table(self, title: str, columns: list, rows: list) -> None:
        table = Table(title=title, title_style="bold cyan", header_style="bold")
        for column in columns:
            table.add_column(column)
        for row in rows:
            table.add_row(*[str(cell) for cell in row])
        self.console.print(table)


def _detail_renderable(detail: str):
    text = detail.strip()
    if text.startswith("---") or text.startswith("+++") or "\n@@" in text or text.startswith("@@"):
        return Syntax(text, "diff", theme="ansi_dark", word_wrap=True)
    lines = text.splitlines()
    if len(lines) > 18:
        text = "\n".join(lines[:18]) + "\n... (" + str(len(lines) - 18) + " more lines)"
    return Text(text, style="dim")


def _one_line(text: str, limit: int) -> str:
    flat = " ".join(str(text).split())
    return flat if len(flat) <= limit else flat[: limit - 3] + "..."


def _mode_label(mode: str) -> str:
    return {
        "ask": "ask - asks before every risky step (default)",
        "auto": "auto - moderate actions run automatically",
        "yolo": "yolo - never asks (blocked actions still blocked)",
    }.get(mode, mode)
