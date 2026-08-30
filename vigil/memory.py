"""Persistent memory: notes Vigil carries across sessions.

Two scopes:
  global  -> valid everywhere (user preferences, working style)
  project -> valid only in that folder (project-specific facts)

Notes live in plain text files; the user can edit them by hand at any time.
"""

from __future__ import annotations

import hashlib
import re
import time
from pathlib import Path

from .config import VIGIL_HOME

MEMORY_DIR = VIGIL_HOME / "memory"
GLOBAL_FILE = MEMORY_DIR / "global.md"
PROJECT_DIR = MEMORY_DIR / "projects"

MAX_ENTRIES = 200
MAX_PROMPT_CHARS = 2500


def project_file(cwd) -> Path:
    """Memory file for a folder. Readable name plus a short hash to avoid collisions."""
    resolved = str(Path(cwd).resolve())
    digest = hashlib.sha1(resolved.encode("utf-8")).hexdigest()[:8]
    name = re.sub(r"[^a-zA-Z0-9_-]", "_", Path(resolved).name) or "root"
    return PROJECT_DIR / (name + "_" + digest + ".md")


def _read(path: Path) -> list:
    if not path.exists():
        return []
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    return [line.strip() for line in lines if line.strip() and not line.strip().startswith("#")]


def _write(path: Path, entries: list, title: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    body = "# " + title + "\n\n" + "\n".join(entries[-MAX_ENTRIES:]) + "\n"
    path.write_text(body, encoding="utf-8")


def add(fact: str, scope: str = "project", cwd=None) -> str:
    """Add a note. Duplicates are ignored."""
    fact = " ".join(str(fact).split())
    if not fact:
        raise ValueError("Cannot store an empty note.")

    path, title = _target(scope, cwd)
    entries = _read(path)
    normalized = fact.lower()
    for entry in entries:
        if normalized in entry.lower():
            return "Already in memory: " + entry[:120]

    entries.append("- [" + time.strftime("%Y-%m-%d") + "] " + fact)
    _write(path, entries, title)
    return "Stored in memory (" + scope + "): " + fact[:150]


def search(query: str = "", scope: str = "all", cwd=None) -> list:
    """Search notes. An empty query returns everything."""
    results = []
    for name, path in _sources(scope, cwd):
        for entry in _read(path):
            if not query or query.lower() in entry.lower():
                results.append((name, entry))
    return results


def remove(match: str, scope: str = "all", cwd=None) -> str:
    """Delete every note containing `match`."""
    match = str(match).strip().lower()
    if not match:
        raise ValueError("Search text cannot be empty.")

    removed = 0
    for name, path in _sources(scope, cwd):
        entries = _read(path)
        kept = [entry for entry in entries if match not in entry.lower()]
        if len(kept) != len(entries):
            removed += len(entries) - len(kept)
            _write(path, kept, _title_for(name))
    if removed == 0:
        return "No matching note found: " + match
    return str(removed) + " note(s) deleted."


def as_prompt(cwd=None) -> str:
    """Memory summary injected into the system prompt. Empty string when there is nothing."""
    parts = []
    global_entries = _read(GLOBAL_FILE)
    project_entries = _read(project_file(cwd or Path.cwd()))

    if global_entries:
        parts.append("General notes:\n" + "\n".join(global_entries[-40:]))
    if project_entries:
        parts.append("Notes for this folder:\n" + "\n".join(project_entries[-40:]))

    if not parts:
        return ""
    text = "\n\n".join(parts)
    if len(text) > MAX_PROMPT_CHARS:
        text = text[-MAX_PROMPT_CHARS:]
        text = text[text.find("\n") + 1 :]
    return text


def stats(cwd=None) -> dict:
    return {
        "global": len(_read(GLOBAL_FILE)),
        "project": len(_read(project_file(cwd or Path.cwd()))),
        "global_file": str(GLOBAL_FILE),
        "project_file": str(project_file(cwd or Path.cwd())),
    }


# ---------------------------------------------------------------- helpers
def _target(scope: str, cwd):
    if scope == "global":
        return GLOBAL_FILE, "Vigil global memory"
    return project_file(cwd or Path.cwd()), "Vigil project memory"


def _sources(scope: str, cwd):
    if scope == "global":
        return [("global", GLOBAL_FILE)]
    if scope == "project":
        return [("project", project_file(cwd or Path.cwd()))]
    return [("global", GLOBAL_FILE), ("project", project_file(cwd or Path.cwd()))]


def _title_for(name: str) -> str:
    return "Vigil global memory" if name == "global" else "Vigil project memory"
