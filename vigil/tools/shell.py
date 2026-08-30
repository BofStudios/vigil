"""Shell tools: running commands and managing the working directory."""

from __future__ import annotations

import os
import platform
import shutil
import subprocess

from ..security import Risk, classify_command
from . import PermissionDenied, ToolContext, ToolError, ToolSpec, truncate

IS_WINDOWS = platform.system() == "Windows"
DEFAULT_TIMEOUT = 90
MAX_TIMEOUT = 900


def _shell_command(command: str, shell: str) -> list:
    """Turn the command into an argv list for the selected shell."""
    choice = (shell or "auto").lower()
    if choice == "auto":
        choice = "powershell" if IS_WINDOWS else "bash"

    if choice == "powershell":
        executable = shutil.which("pwsh") or shutil.which("powershell") or "powershell"
        return [executable, "-NoProfile", "-NonInteractive", "-Command", command]
    if choice == "cmd":
        return ["cmd", "/c", command]
    if choice in ("bash", "sh"):
        executable = shutil.which(choice) or choice
        return [executable, "-lc", command]
    raise ToolError("Unknown shell: " + str(shell))


def run_command(
    ctx: ToolContext,
    command: str,
    cwd: str = "",
    timeout: int = DEFAULT_TIMEOUT,
    shell: str = "auto",
) -> str:
    """Run a shell command. Approval is requested based on the risk level."""
    command = (command or "").strip()
    if not command:
        raise ToolError("Empty command.")

    work_dir = ctx.resolve(cwd) if cwd else ctx.cwd
    if not work_dir.is_dir():
        raise ToolError("Working directory not found: " + str(work_dir))

    verdict = classify_command(command)
    detail = "shell: " + (shell if shell != "auto" else ("powershell" if IS_WINDOWS else "bash"))
    detail += "\ndirectory: " + str(work_dir)
    if verdict.reason:
        detail += "\nassessment: " + verdict.reason

    allowed, reason = ctx.guard.check_command("run_command", command, detail=detail)
    if not allowed:
        raise PermissionDenied(reason)

    argv = _shell_command(command, shell)
    timeout = max(1, min(int(timeout or DEFAULT_TIMEOUT), MAX_TIMEOUT))

    env = os.environ.copy()
    env.setdefault("PYTHONIOENCODING", "utf-8")
    env.setdefault("NO_COLOR", "1")

    try:
        completed = subprocess.run(
            argv,
            cwd=str(work_dir),
            capture_output=True,
            timeout=timeout,
            env=env,
            stdin=subprocess.DEVNULL,
        )
    except subprocess.TimeoutExpired:
        raise ToolError(
            "The command did not finish within " + str(timeout) + " seconds and was stopped. "
            "Raise the timeout for long jobs, or start them in the background."
        ) from None
    except FileNotFoundError as exc:
        raise ToolError("Shell not found: " + str(exc)) from exc
    except OSError as exc:
        raise ToolError("Could not run the command: " + str(exc)) from exc

    stdout = _decode(completed.stdout)
    stderr = _decode(completed.stderr)

    parts = ["exit code: " + str(completed.returncode)]
    if stdout.strip():
        parts.append("stdout:\n" + stdout.strip())
    if stderr.strip():
        parts.append("stderr:\n" + stderr.strip())
    if not stdout.strip() and not stderr.strip():
        parts.append("(no output)")

    return truncate("\n\n".join(parts), ctx.config.max_tool_output)


def change_dir(ctx: ToolContext, path: str) -> str:
    """Change the working directory of the session."""
    target = ctx.resolve(path)
    if not target.is_dir():
        raise ToolError("Directory not found: " + str(target))
    ctx.cwd = target.resolve()
    return "Working directory: " + str(ctx.cwd)


def current_dir(ctx: ToolContext) -> str:
    """Return the current working directory."""
    return str(ctx.cwd)


def _decode(data) -> str:
    if data is None:
        return ""
    if isinstance(data, str):
        return data
    for encoding in ("utf-8", "cp850", "cp1252", "latin-1"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", errors="replace")


def _risk_of(args: dict) -> Risk:
    return classify_command(str(args.get("command", ""))).risk


TOOLS = [
    ToolSpec(
        name="run_command",
        description=(
            "Run a terminal command. PowerShell on Windows, bash elsewhere. "
            "Do not use interactive commands - anything waiting for input will time out."
        ),
        parameters={
            "type": "object",
            "properties": {
                "command": {"type": "string", "description": "The command to run"},
                "cwd": {"type": "string", "description": "Working directory (defaults to the current one)"},
                "timeout": {"type": "integer", "description": "Timeout in seconds", "default": 90},
                "shell": {
                    "type": "string",
                    "enum": ["auto", "powershell", "cmd", "bash", "sh"],
                    "default": "auto",
                },
            },
            "required": ["command"],
        },
        handler=run_command,
        group="terminal",
        risk=Risk.MODERATE,
        risk_fn=_risk_of,
        preview=lambda a: str(a.get("command", "")),
    ),
    ToolSpec(
        name="change_dir",
        description="Change the working directory of the session.",
        parameters={
            "type": "object",
            "properties": {"path": {"type": "string"}},
            "required": ["path"],
        },
        handler=change_dir,
        group="terminal",
        risk=Risk.SAFE,
        preview=lambda a: "cd " + str(a.get("path", "")),
    ),
    ToolSpec(
        name="current_dir",
        description="Show the current working directory.",
        parameters={"type": "object", "properties": {}},
        handler=current_dir,
        group="terminal",
        risk=Risk.SAFE,
        preview=lambda a: "pwd",
    ),
]
