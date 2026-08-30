"""Task planner tests."""

import pytest

from vigil.config import Config
from vigil.security import Guard
from vigil.tools import ToolContext, ToolError, planner


@pytest.fixture
def ctx(tmp_path):
    config = Config()
    guard = Guard(mode="yolo", confirm=None, audit=False)
    return ToolContext(config=config, guard=guard, provider=None, ui=None, cwd=tmp_path)


def test_create_plan_marks_the_first_step_in_progress(ctx):
    result = planner.create_plan(ctx, ["read the file", "edit it", "run the tests"])
    steps = ctx.state["plan"]

    assert len(steps) == 3
    assert steps[0]["status"] == "doing"
    assert steps[1]["status"] == "todo"
    assert "3 steps" in result


def test_create_plan_rejects_an_empty_list(ctx):
    with pytest.raises(ToolError):
        planner.create_plan(ctx, [])


def test_create_plan_rejects_too_many_steps(ctx):
    with pytest.raises(ToolError):
        planner.create_plan(ctx, ["step " + str(i) for i in range(planner.MAX_STEPS + 1)])


def test_blank_steps_are_dropped(ctx):
    planner.create_plan(ctx, ["real step", "   ", ""])
    assert len(ctx.state["plan"]) == 1


def test_long_step_text_is_shortened(ctx):
    planner.create_plan(ctx, ["x" * 400])
    assert len(ctx.state["plan"][0]["text"]) <= planner.MAX_STEP_LENGTH


def test_completing_a_step_starts_the_next_one(ctx):
    planner.create_plan(ctx, ["first", "second", "third"])
    planner.update_plan(ctx, 1, "done")

    steps = ctx.state["plan"]
    assert steps[0]["status"] == "done"
    assert steps[1]["status"] == "doing"
    assert steps[2]["status"] == "todo"


def test_note_is_stored(ctx):
    planner.create_plan(ctx, ["only step"])
    planner.update_plan(ctx, 1, "blocked", note="permission denied")
    assert ctx.state["plan"][0]["note"] == "permission denied"
    assert ctx.state["plan"][0]["status"] == "blocked"


def test_finishing_every_step_is_reported(ctx):
    planner.create_plan(ctx, ["a", "b"])
    planner.update_plan(ctx, 1, "done")
    result = planner.update_plan(ctx, 2, "done")
    assert "Every step is finished" in result


def test_update_without_a_plan_fails(ctx):
    with pytest.raises(ToolError):
        planner.update_plan(ctx, 1, "done")


def test_unknown_status_is_rejected(ctx):
    planner.create_plan(ctx, ["a"])
    with pytest.raises(ToolError):
        planner.update_plan(ctx, 1, "finished-ish")


def test_out_of_range_step_is_rejected(ctx):
    planner.create_plan(ctx, ["a", "b"])
    with pytest.raises(ToolError):
        planner.update_plan(ctx, 5, "done")


def test_show_plan_without_a_plan(ctx):
    assert "No plan yet" in planner.show_plan(ctx)


def test_show_plan_reports_progress(ctx):
    planner.create_plan(ctx, ["a", "b", "c"])
    planner.update_plan(ctx, 1, "done")
    output = planner.show_plan(ctx)
    assert "1/3 steps complete" in output
    assert "[x]" in output and "[>]" in output


def test_plan_is_rendered_through_the_ui(ctx):
    class RecordingUI:
        def __init__(self):
            self.rendered = []

        def plan(self, steps):
            self.rendered.append(list(steps))

    ctx.ui = RecordingUI()
    planner.create_plan(ctx, ["a", "b"])
    planner.update_plan(ctx, 1, "done")

    assert len(ctx.ui.rendered) == 2
    assert ctx.ui.rendered[-1][0]["status"] == "done"
