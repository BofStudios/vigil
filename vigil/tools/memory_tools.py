"""Memory tools: let Vigil carry knowledge across sessions."""

from __future__ import annotations

from .. import memory
from ..security import Risk
from . import PermissionDenied, ToolContext, ToolError, ToolSpec, truncate

SCOPES = ("project", "global")


def remember(ctx: ToolContext, fact: str, scope: str = "project") -> str:
    if scope not in SCOPES:
        raise ToolError("scope must be 'project' or 'global'.")
    try:
        return memory.add(fact, scope=scope, cwd=ctx.cwd)
    except (ValueError, OSError) as exc:
        raise ToolError("Could not write to memory: " + str(exc)) from exc


def recall(ctx: ToolContext, query: str = "", scope: str = "all") -> str:
    results = memory.search(query, scope=scope, cwd=ctx.cwd)
    if not results:
        return "Nothing in memory matches" + ((": " + query) if query else ".")
    lines = [name + ": " + entry for name, entry in results]
    return str(len(results)) + " note(s):\n" + truncate("\n".join(lines), ctx.config.max_tool_output)


def forget(ctx: ToolContext, match: str, scope: str = "all") -> str:
    hits = memory.search(match, scope=scope, cwd=ctx.cwd)
    if not hits:
        return "No matching note found: " + match

    preview = "\n".join(name + ": " + entry for name, entry in hits[:10])
    allowed, reason = ctx.guard.check_action(
        "forget",
        str(len(hits)) + " note(s) will be deleted",
        Risk.MODERATE,
        "permanent deletion from memory",
        detail=preview,
    )
    if not allowed:
        raise PermissionDenied(reason)

    try:
        return memory.remove(match, scope=scope, cwd=ctx.cwd)
    except (ValueError, OSError) as exc:
        raise ToolError("Delete failed: " + str(exc)) from exc


TOOLS = [
    ToolSpec(
        name="remember",
        description=(
            "Store a fact that should still be known in future sessions. "
            "Use it for user preferences, project rules and recurring facts. "
            "Do not store temporary or trivial details."
        ),
        parameters={
            "type": "object",
            "properties": {
                "fact": {"type": "string", "description": "The fact to remember, one sentence"},
                "scope": {
                    "type": "string",
                    "enum": list(SCOPES),
                    "description": "project = this folder only, global = everywhere",
                    "default": "project",
                },
            },
            "required": ["fact"],
        },
        handler=remember,
        group="memory",
        risk=Risk.SAFE,
        preview=lambda a: "remember: " + str(a.get("fact", ""))[:70],
    ),
    ToolSpec(
        name="recall",
        description="Search stored notes. An empty query returns every note.",
        parameters={
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "Word to search for (empty = all)"},
                "scope": {"type": "string", "enum": ["all", "project", "global"], "default": "all"},
            },
        },
        handler=recall,
        group="memory",
        risk=Risk.SAFE,
        preview=lambda a: "recall: " + str(a.get("query", "everything")),
    ),
    ToolSpec(
        name="forget",
        description="Delete stored notes that contain the given text.",
        parameters={
            "type": "object",
            "properties": {
                "match": {"type": "string", "description": "Text contained in the notes to delete"},
                "scope": {"type": "string", "enum": ["all", "project", "global"], "default": "all"},
            },
            "required": ["match"],
        },
        handler=forget,
        group="memory",
        risk=Risk.MODERATE,
        preview=lambda a: "forget: " + str(a.get("match", "")),
    ),
]
