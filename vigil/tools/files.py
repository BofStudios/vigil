"""Filesystem tools: read, write, edit, search, move, delete."""

from __future__ import annotations

import fnmatch
import os
import re
import shutil
from pathlib import Path

from ..security import Risk
from . import PermissionDenied, ToolContext, ToolError, ToolSpec, truncate

MAX_READ_BYTES = 400_000
SKIP_DIRS = {
    ".git", "node_modules", "__pycache__", ".venv", "venv", "dist", "build",
    ".next", ".idea", ".pytest_cache", ".ruff_cache", "site-packages", ".cache",
}
BINARY_SUFFIXES = {
    ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".mp3", ".mp4", ".avi",
    ".mkv", ".zip", ".rar", ".7z", ".gz", ".tar", ".exe", ".dll", ".so", ".dylib",
    ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pyc", ".class", ".bin",
}


def _require(ctx: ToolContext, tool: str, path, write: bool = False, detail: str = "") -> Path:
    resolved = ctx.resolve(str(path))
    allowed, reason = ctx.guard.check_path(tool, str(resolved), write=write, detail=detail)
    if not allowed:
        raise PermissionDenied(reason)
    return resolved


def _read_text(path: Path) -> str:
    for encoding in ("utf-8", "utf-8-sig", "cp1252", "latin-1"):
        try:
            return path.read_text(encoding=encoding)
        except UnicodeDecodeError:
            continue
        except OSError as exc:
            raise ToolError("Could not read the file: " + str(exc)) from exc
    raise ToolError("File could not be decoded as text (it may be binary).")


# ---------------------------------------------------------------- read_file
def read_file(ctx: ToolContext, path: str, offset: int = 1, limit: int = 400) -> str:
    target = _require(ctx, "read_file", path)
    if not target.exists():
        raise ToolError("File not found: " + str(target))
    if target.is_dir():
        raise ToolError(str(target) + " is a directory. Use list_dir.")
    if target.suffix.lower() in BINARY_SUFFIXES:
        return "[binary file] " + str(target) + " (" + _human(target.stat().st_size) + ") - not readable as text."
    if target.stat().st_size > MAX_READ_BYTES:
        raise ToolError(
            "File is too large (" + _human(target.stat().st_size) + "). Read it in chunks with "
            "offset/limit, or use search_text."
        )

    lines = _read_text(target).splitlines()
    start = max(1, int(offset)) - 1
    end = start + max(1, int(limit))
    chunk = lines[start:end]
    numbered = "\n".join(str(start + i + 1) + "\t" + line for i, line in enumerate(chunk))
    header = str(target) + " (" + str(len(lines)) + " lines"
    if end < len(lines):
        header += ", showing " + str(start + 1) + "-" + str(min(end, len(lines)))
    header += ")"
    return header + "\n" + truncate(numbered, ctx.config.max_tool_output)


# --------------------------------------------------------------- write_file
def write_file(ctx: ToolContext, path: str, content: str) -> str:
    target = ctx.resolve(path)
    exists = target.exists()
    if exists:
        try:
            detail = _diff_preview(_read_text(target), content)
        except ToolError:
            detail = "(existing content could not be previewed)"
    else:
        detail = _preview_block(content)

    target = _require(ctx, "write_file", target, write=True, detail=detail)
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
    except OSError as exc:
        raise ToolError("Write failed: " + str(exc)) from exc

    action = "updated" if exists else "created"
    return str(target) + " " + action + " (" + str(len(content.splitlines())) + " lines)."


# ---------------------------------------------------------------- edit_file
def edit_file(ctx: ToolContext, path: str, old_text: str, new_text: str, replace_all: bool = False) -> str:
    target = ctx.resolve(path)
    if not target.exists():
        raise ToolError("File not found: " + str(target))
    original = _read_text(target)
    count = original.count(old_text)
    if count == 0:
        raise ToolError("The text was not found in the file. Read it first to check the exact content.")
    if count > 1 and not replace_all:
        raise ToolError(
            "The text appears " + str(count) + " times. Add more context or pass replace_all=true."
        )

    updated = original.replace(old_text, new_text) if replace_all else original.replace(old_text, new_text, 1)
    detail = _diff_preview(original, updated)
    target = _require(ctx, "edit_file", target, write=True, detail=detail)
    try:
        target.write_text(updated, encoding="utf-8")
    except OSError as exc:
        raise ToolError("Write failed: " + str(exc)) from exc
    return str(target) + " edited (" + str(count if replace_all else 1) + " replacement(s))."


# ----------------------------------------------------------------- list_dir
def list_dir(ctx: ToolContext, path: str = ".", show_hidden: bool = False, limit: int = 200) -> str:
    target = _require(ctx, "list_dir", path)
    if not target.exists():
        raise ToolError("Directory not found: " + str(target))
    if not target.is_dir():
        return read_file(ctx, str(target))

    entries = []
    try:
        for item in sorted(target.iterdir(), key=lambda p: (p.is_file(), p.name.lower())):
            if not show_hidden and item.name.startswith("."):
                continue
            try:
                if item.is_dir():
                    entries.append("[dir]  " + item.name + "/")
                else:
                    entries.append("[file] " + item.name + "  " + _human(item.stat().st_size))
            except OSError:
                entries.append("[?]    " + item.name)
    except PermissionError as exc:
        raise ToolError("Permission denied for this directory: " + str(exc)) from exc

    total = len(entries)
    shown = entries[: int(limit)]
    header = str(target) + " - " + str(total) + " item(s)"
    if total > len(shown):
        header += " (showing first " + str(len(shown)) + ")"
    return header + "\n" + "\n".join(shown)


# --------------------------------------------------------------- find_files
def find_files(ctx: ToolContext, pattern: str, path: str = ".", limit: int = 100) -> str:
    root = _require(ctx, "find_files", path)
    if not root.is_dir():
        raise ToolError("Not a directory: " + str(root))

    matches = []
    for current, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not d.startswith(".")]
        for name in files:
            if fnmatch.fnmatch(name, pattern):
                matches.append(str(Path(current) / name))
                if len(matches) >= int(limit):
                    break
        if len(matches) >= int(limit):
            break

    if not matches:
        return "No matches for " + pattern + " in " + str(root)
    return str(len(matches)) + " match(es):\n" + "\n".join(matches)


# -------------------------------------------------------------- search_text
def search_text(
    ctx: ToolContext,
    query: str,
    path: str = ".",
    file_pattern: str = "*",
    ignore_case: bool = True,
    limit: int = 80,
) -> str:
    root = _require(ctx, "search_text", path)
    try:
        regex = re.compile(query, re.IGNORECASE if ignore_case else 0)
    except re.error as exc:
        raise ToolError("Invalid search pattern: " + str(exc)) from exc

    hits = []
    files = [root] if root.is_file() else []
    if root.is_dir():
        for current, dirs, names in os.walk(root):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not d.startswith(".")]
            for name in names:
                if fnmatch.fnmatch(name, file_pattern):
                    candidate = Path(current) / name
                    if candidate.suffix.lower() not in BINARY_SUFFIXES:
                        files.append(candidate)

    for candidate in files:
        try:
            if candidate.stat().st_size > MAX_READ_BYTES:
                continue
            with open(candidate, encoding="utf-8", errors="ignore") as handle:
                for number, line in enumerate(handle, 1):
                    if regex.search(line):
                        hits.append(str(candidate) + ":" + str(number) + ": " + line.rstrip()[:220])
                        if len(hits) >= int(limit):
                            break
        except OSError:
            continue
        if len(hits) >= int(limit):
            break

    if not hits:
        return "No results for: " + query
    return str(len(hits)) + " result(s):\n" + truncate("\n".join(hits), ctx.config.max_tool_output)


# ---------------------------------------------------------------- move_path
def move_path(ctx: ToolContext, source: str, destination: str) -> str:
    src = _require(ctx, "move_path", source, write=True)
    dst = _require(ctx, "move_path", destination, write=True, detail=str(src) + " -> " + str(destination))
    if not src.exists():
        raise ToolError("Source not found: " + str(src))
    try:
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(src), str(dst))
    except OSError as exc:
        raise ToolError("Move failed: " + str(exc)) from exc
    return str(src) + " -> " + str(dst) + " moved."


# ---------------------------------------------------------------- copy_path
def copy_path(ctx: ToolContext, source: str, destination: str) -> str:
    src = _require(ctx, "copy_path", source)
    dst = _require(ctx, "copy_path", destination, write=True, detail=str(src) + " -> " + str(destination))
    if not src.exists():
        raise ToolError("Source not found: " + str(src))
    try:
        if src.is_dir():
            shutil.copytree(str(src), str(dst), dirs_exist_ok=True)
        else:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(str(src), str(dst))
    except OSError as exc:
        raise ToolError("Copy failed: " + str(exc)) from exc
    return str(src) + " -> " + str(dst) + " copied."


# -------------------------------------------------------------- delete_path
def delete_path(ctx: ToolContext, path: str, recursive: bool = False) -> str:
    target = ctx.resolve(path)
    if not target.exists():
        raise ToolError("Not found: " + str(target))

    if target.is_dir():
        count = sum(1 for _ in target.rglob("*"))
        detail = "DIRECTORY TO DELETE: " + str(target) + " (" + str(count) + " item(s) inside)"
        if count > 0 and not recursive:
            raise ToolError("Directory is not empty. Pass recursive=true to delete it.")
    else:
        detail = "FILE TO DELETE: " + str(target) + " (" + _human(target.stat().st_size) + ")"

    allowed, reason = ctx.guard.check_action(
        "delete_path", detail, Risk.HIGH, "permanent deletion", detail=detail
    )
    if not allowed:
        raise PermissionDenied(reason)
    # The system-directory check still applies after approval.
    _require(ctx, "delete_path", target, write=True)

    try:
        if target.is_dir():
            shutil.rmtree(target)
        else:
            target.unlink()
    except OSError as exc:
        raise ToolError("Delete failed: " + str(exc)) from exc
    return str(target) + " deleted."


# ----------------------------------------------------------------- make_dir
def make_dir(ctx: ToolContext, path: str) -> str:
    target = _require(ctx, "make_dir", path, write=True)
    try:
        target.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise ToolError("Could not create the directory: " + str(exc)) from exc
    return str(target) + " is ready."


# ------------------------------------------------------------------ helpers
def _human(size: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return (str(int(size)) if unit == "B" else format(size, ".1f")) + " " + unit
        size /= 1024
    return str(size)


def _preview_block(content: str, limit: int = 24) -> str:
    lines = content.splitlines()
    text = "\n".join("+ " + line for line in lines[:limit])
    if len(lines) > limit:
        text += "\n+ ... (" + str(len(lines) - limit) + " more lines)"
    return text


def _diff_preview(old: str, new: str, context: int = 3) -> str:
    import difflib

    diff = list(
        difflib.unified_diff(
            old.splitlines(), new.splitlines(), fromfile="current", tofile="new", lineterm="", n=context
        )
    )
    if not diff:
        return "(content is identical)"
    if len(diff) > 60:
        diff = diff[:60] + ["... (diff truncated)"]
    return "\n".join(diff)


TOOLS = [
    ToolSpec(
        name="read_file",
        description="Read a file with line numbers. Use offset and limit for large files.",
        parameters={
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "File path (relative or absolute)"},
                "offset": {"type": "integer", "description": "Starting line (1-based)", "default": 1},
                "limit": {"type": "integer", "description": "How many lines to read", "default": 400},
            },
            "required": ["path"],
        },
        handler=read_file,
        group="file",
        risk=Risk.SAFE,
        preview=lambda a: "read: " + str(a.get("path", "")),
    ),
    ToolSpec(
        name="write_file",
        description="Write content to a file. Overwrites the whole file; prefer edit_file for small changes.",
        parameters={
            "type": "object",
            "properties": {
                "path": {"type": "string"},
                "content": {"type": "string", "description": "Full new content of the file"},
            },
            "required": ["path", "content"],
        },
        handler=write_file,
        group="file",
        risk=Risk.MODERATE,
        preview=lambda a: "write: " + str(a.get("path", "")),
    ),
    ToolSpec(
        name="edit_file",
        description="Replace a specific piece of text in a file. old_text must match exactly.",
        parameters={
            "type": "object",
            "properties": {
                "path": {"type": "string"},
                "old_text": {"type": "string", "description": "Existing text to replace (exact match)"},
                "new_text": {"type": "string", "description": "Replacement text"},
                "replace_all": {"type": "boolean", "default": False},
            },
            "required": ["path", "old_text", "new_text"],
        },
        handler=edit_file,
        group="file",
        risk=Risk.MODERATE,
        preview=lambda a: "edit: " + str(a.get("path", "")),
    ),
    ToolSpec(
        name="list_dir",
        description="List the contents of a directory.",
        parameters={
            "type": "object",
            "properties": {
                "path": {"type": "string", "default": "."},
                "show_hidden": {"type": "boolean", "default": False},
                "limit": {"type": "integer", "default": 200},
            },
        },
        handler=list_dir,
        group="file",
        risk=Risk.SAFE,
        preview=lambda a: "list: " + str(a.get("path", ".")),
    ),
    ToolSpec(
        name="find_files",
        description="Search recursively by filename pattern. Examples: *.py, report*.pdf",
        parameters={
            "type": "object",
            "properties": {
                "pattern": {"type": "string", "description": "Glob pattern, e.g. *.txt"},
                "path": {"type": "string", "default": "."},
                "limit": {"type": "integer", "default": 100},
            },
            "required": ["pattern"],
        },
        handler=find_files,
        group="file",
        risk=Risk.SAFE,
        preview=lambda a: "find files: " + str(a.get("pattern", "")),
    ),
    ToolSpec(
        name="search_text",
        description="Search for text or a regular expression inside files (grep-like).",
        parameters={
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "Text or regex to find"},
                "path": {"type": "string", "default": "."},
                "file_pattern": {"type": "string", "default": "*"},
                "ignore_case": {"type": "boolean", "default": True},
                "limit": {"type": "integer", "default": 80},
            },
            "required": ["query"],
        },
        handler=search_text,
        group="file",
        risk=Risk.SAFE,
        preview=lambda a: "search: " + str(a.get("query", "")),
    ),
    ToolSpec(
        name="make_dir",
        description="Create a directory, including parent directories.",
        parameters={
            "type": "object",
            "properties": {"path": {"type": "string"}},
            "required": ["path"],
        },
        handler=make_dir,
        group="file",
        risk=Risk.MODERATE,
        preview=lambda a: "mkdir: " + str(a.get("path", "")),
    ),
    ToolSpec(
        name="copy_path",
        description="Copy a file or directory.",
        parameters={
            "type": "object",
            "properties": {"source": {"type": "string"}, "destination": {"type": "string"}},
            "required": ["source", "destination"],
        },
        handler=copy_path,
        group="file",
        risk=Risk.MODERATE,
        preview=lambda a: "copy: " + str(a.get("source", "")) + " -> " + str(a.get("destination", "")),
    ),
    ToolSpec(
        name="move_path",
        description="Move or rename a file or directory.",
        parameters={
            "type": "object",
            "properties": {"source": {"type": "string"}, "destination": {"type": "string"}},
            "required": ["source", "destination"],
        },
        handler=move_path,
        group="file",
        risk=Risk.MODERATE,
        preview=lambda a: "move: " + str(a.get("source", "")) + " -> " + str(a.get("destination", "")),
    ),
    ToolSpec(
        name="delete_path",
        description="Permanently delete a file or directory. Always asks for confirmation.",
        parameters={
            "type": "object",
            "properties": {
                "path": {"type": "string"},
                "recursive": {"type": "boolean", "description": "true for non-empty directories", "default": False},
            },
            "required": ["path"],
        },
        handler=delete_path,
        group="file",
        risk=Risk.HIGH,
        preview=lambda a: "DELETE: " + str(a.get("path", "")),
    ),
]
