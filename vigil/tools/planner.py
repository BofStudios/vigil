"""Task planning: an explicit checklist the model keeps and the user can watch.

Long jobs are where agents drift - they forget a step, redo one, or stop early.
A visible plan fixes all three: the model writes the steps down before it starts,
marks them off as it goes, and both sides can see what is left.

The plan lives in the session only; it is not written to disk.
"""

from __future__ import annotations

from ..security import Risk
from . import ToolContext, ToolError, ToolSpec

STATE_KEY = "plan"
MAX_STEPS = 20
MAX_STEP_LENGTH = 160

STATUSES = ("todo", "doing", "done", "blocked")
ICONS = {"todo": "[ ]", "doing": "[>]", "done": "[x]", "blocked": "[!]"}


def _plan(ctx: ToolContext) -> list:
    return ctx.state.get(STATE_KEY) or []


def _render(ctx: ToolContext, steps: list) -> str:
    if not steps:
        return "No plan yet."
    lines = []
    for index, step in enumerate(steps, 1):
        line = ICONS.get(step["status"], "[ ]") + " " + str(index) + ". " + step["text"]
        if step.get("note"):
            line += "  (" + step["note"] + ")"
        lines.append(line)
    done = sum(1 for step in steps if step["status"] == "done")
    lines.append("")
    lines.append(str(done) + "/" + str(len(steps)) + " steps complete")
    return "\n".join(lines)


def _show(ctx: ToolContext, steps: list) -> None:
    """Render the plan in the terminal if a UI is attached."""
    if ctx.ui is not None and hasattr(ctx.ui, "plan"):
        ctx.ui.plan(steps)


# ---------------------------------------------------------------- create_plan
def create_plan(ctx: ToolContext, steps: list) -> str:
    if not isinstance(steps, list) or not steps:
        raise ToolError("steps must be a non-empty list of strings.")
    if len(steps) > MAX_STEPS:
        raise ToolError(
            "A plan may have at most " + str(MAX_STEPS) + " steps. Group the small ones together."
        )

    plan = []
    for raw in steps:
        text = " ".join(str(raw).split())
        if not text:
            continue
        if len(text) > MAX_STEP_LENGTH:
            text = text[: MAX_STEP_LENGTH - 3] + "..."
        plan.append({"text": text, "status": "todo", "note": ""})

    if not plan:
        raise ToolError("All steps were empty.")

    plan[0]["status"] = "doing"
    ctx.state[STATE_KEY] = plan
    _show(ctx, plan)
    return "Plan created with " + str(len(plan)) + " steps. Step 1 is in progress.\n" + _render(ctx, plan)


# ---------------------------------------------------------------- update_plan
def update_plan(ctx: ToolContext, step: int, status: str = "done", note: str = "") -> str:
    plan = _plan(ctx)
    if not plan:
        raise ToolError("There is no plan yet. Use create_plan first.")

    status = str(status).lower().strip()
    if status not in STATUSES:
        raise ToolError("status must be one of: " + ", ".join(STATUSES))

    try:
        index = int(step) - 1
    except (TypeError, ValueError) as exc:
        raise ToolError("step must be a step number (1-based).") from exc
    if not 0 <= index < len(plan):
        raise ToolError("There is no step " + str(step) + ". The plan has " + str(len(plan)) + " steps.")

    plan[index]["status"] = status
    if note:
        plan[index]["note"] = " ".join(str(note).split())[:120]

    # Move on to the next unfinished step automatically.
    if status == "done":
        for entry in plan:
            if entry["status"] == "todo":
                entry["status"] = "doing"
                break

    ctx.state[STATE_KEY] = plan
    _show(ctx, plan)

    remaining = sum(1 for entry in plan if entry["status"] in ("todo", "doing"))
    if remaining == 0:
        return "Step " + str(step) + " marked " + status + ". Every step is finished.\n" + _render(ctx, plan)
    return (
        "Step " + str(step) + " marked " + status + ". " + str(remaining) + " step(s) left.\n"
        + _render(ctx, plan)
    )


# ------------------------------------------------------------------ show_plan
def show_plan(ctx: ToolContext) -> str:
    plan = _plan(ctx)
    if plan:
        _show(ctx, plan)
    return _render(ctx, plan)


TOOLS = [
    ToolSpec(
        name="create_plan",
        description=(
            "Write down a step-by-step plan before starting a job that needs three or more steps. "
            "Keep steps short and concrete - one action each. The user sees the plan, so it is also "
            "how you tell them what you are about to do. Do not use it for single-step requests."
        ),
        parameters={
            "type": "object",
            "properties": {
                "steps": {
                    "type": "array",
                    "items": {"type": "string"},
                    "description": "The steps in order, one short sentence each",
                }
            },
            "required": ["steps"],
        },
        handler=create_plan,
        group="planning",
        risk=Risk.SAFE,
        quiet_result=True,
        preview=lambda a: "plan: " + str(len(a.get("steps") or [])) + " steps",
    ),
    ToolSpec(
        name="update_plan",
        description=(
            "Mark a step done, in progress or blocked. Call it as soon as a step finishes, not in "
            "a batch at the end. When a step is marked done the next one starts automatically."
        ),
        parameters={
            "type": "object",
            "properties": {
                "step": {"type": "integer", "description": "Step number, starting at 1"},
                "status": {"type": "string", "enum": list(STATUSES), "default": "done"},
                "note": {"type": "string", "description": "Short result or reason, optional"},
            },
            "required": ["step"],
        },
        handler=update_plan,
        group="planning",
        risk=Risk.SAFE,
        quiet_result=True,
        preview=lambda a: "step " + str(a.get("step", "?")) + " -> " + str(a.get("status", "done")),
    ),
    ToolSpec(
        name="show_plan",
        description="Show the current plan and what is left of it.",
        parameters={"type": "object", "properties": {}},
        handler=show_plan,
        group="planning",
        risk=Risk.SAFE,
        quiet_result=True,
        preview=lambda a: "show plan",
    ),
]
