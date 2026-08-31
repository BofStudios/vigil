"""Desktop layer tests.

These exercise the bridge and the session wiring without opening a window, so
they run in CI where pywebview is not installed. `vigil.desktop.app` only imports
webview inside the functions that need it, which keeps that possible.
"""

import threading

import pytest

from vigil.config import Config
from vigil.desktop.app import Api, _js_literal
from vigil.desktop.session import DesktopUI
from vigil.security import Action, Risk, Verdict


@pytest.fixture
def config():
    settings = Config()
    settings.api_key = "test"
    settings.provider = "groq"
    return settings


# ------------------------------------------------------------- js literals
def test_js_literal_escapes_line_separators():
    payload = {"text": "before" + chr(0x2028) + "after" + chr(0x2029) + "end"}
    encoded = _js_literal(payload)
    assert chr(0x2028) not in encoded
    assert chr(0x2029) not in encoded
    assert "\\u2028" in encoded and "\\u2029" in encoded


def test_js_literal_handles_quotes_and_backslashes():
    encoded = _js_literal({"text": 'C:\\Users\\Pc "quoted"'})
    assert encoded.startswith("{") and encoded.endswith("}")
    import json

    assert json.loads(encoded)["text"] == 'C:\\Users\\Pc "quoted"'


# ------------------------------------------------------------------- events
def test_ui_events_carry_the_tab_and_type():
    seen = []
    ui = DesktopUI(seen.append, "tab-1")

    ui.stream_chunk("hello")
    ui.tool_start("run_command", "ls -la", Risk.SAFE)
    ui.tool_result("output", ok=True)
    ui.plan([{"text": "step", "status": "doing", "note": ""}])
    ui.warn("careful")

    kinds = [event["type"] for event in seen]
    assert kinds == ["assistant_chunk", "tool", "tool_result", "plan", "notice"]
    assert all(event["tab"] == "tab-1" for event in seen)
    assert seen[1]["risk"] == "safe"
    assert seen[4]["level"] == "warn"


def test_plan_events_are_copied_not_referenced():
    seen = []
    ui = DesktopUI(seen.append, "tab-1")
    steps = [{"text": "one", "status": "todo", "note": ""}]
    ui.plan(steps)

    steps[0]["status"] = "done"  # mutating afterwards must not rewrite the event
    assert seen[0]["steps"][0]["status"] == "todo"


# ---------------------------------------------------------------- approvals
def _action(risk=Risk.HIGH):
    return Action("delete_path", "delete everything", Verdict(risk, "permanent deletion"))


def test_confirm_blocks_until_answered():
    seen = []
    ui = DesktopUI(seen.append, "tab-1")
    result = {}

    worker = threading.Thread(target=lambda: result.update(answer=ui.confirm(_action())))
    worker.start()

    # the request reaches the front end...
    for _ in range(200):
        if seen:
            break
        threading.Event().wait(0.01)
    assert seen and seen[0]["type"] == "approval"
    request_id = seen[0]["request"]

    # ...and the worker is still parked until we reply
    assert worker.is_alive()
    assert ui.answer(request_id, "yes") is True
    worker.join(timeout=2)
    assert result["answer"] == "yes"


def test_unknown_answer_is_treated_as_no():
    seen = []
    ui = DesktopUI(seen.append, "tab-1")
    result = {}
    worker = threading.Thread(target=lambda: result.update(answer=ui.confirm(_action())))
    worker.start()
    for _ in range(200):
        if seen:
            break
        threading.Event().wait(0.01)

    ui.answer(seen[0]["request"], "maybe")
    worker.join(timeout=2)
    assert result["answer"] == "no"


def test_answering_an_unknown_request_is_ignored():
    ui = DesktopUI(lambda event: None, "tab-1")
    assert ui.answer("req-does-not-exist", "yes") is False


def test_release_all_unblocks_a_pending_question():
    seen = []
    ui = DesktopUI(seen.append, "tab-1")
    result = {}
    worker = threading.Thread(target=lambda: result.update(answer=ui.confirm(_action())))
    worker.start()
    for _ in range(200):
        if seen:
            break
        threading.Event().wait(0.01)

    ui.release_all()  # what closing a tab mid-question does
    worker.join(timeout=2)
    assert result["answer"] == "no"


# --------------------------------------------------------------------- api
def test_api_creates_and_closes_tabs(config):
    api = Api(config)
    first = api.new_tab()
    assert first["id"] in api.sessions
    assert first["tools"] > 0

    second = api.new_tab()
    assert len(api.sessions) == 2

    api.close_tab(first["id"])
    assert first["id"] not in api.sessions
    assert second["id"] in api.sessions


def test_closing_the_last_tab_opens_a_fresh_one(config):
    api = Api(config)
    only = api.new_tab()
    api.close_tab(only["id"])
    assert len(api.sessions) == 1
    assert only["id"] not in api.sessions


def test_send_to_unknown_tab_reports_an_error(config):
    assert "error" in Api(config).send("tab-nope", "hello")


def test_mode_change_reaches_every_session(config):
    api = Api(config)
    api.new_tab()
    api.new_tab()

    assert api.set_mode("auto") == {"mode": "auto"}
    assert all(session.agent.guard.mode == "auto" for session in api.sessions.values())


def test_invalid_mode_is_rejected(config):
    api = Api(config)
    api.new_tab()
    assert "error" in api.set_mode("reckless")


def test_state_describes_the_window(config):
    api = Api(config)
    api.new_tab()
    state = api.state()
    assert state["provider"] == "groq"
    assert state["mode"] in ("ask", "auto", "yolo")
    assert len(state["tabs"]) == 1


def test_tools_are_grouped(config):
    api = Api(config)
    tab = api.new_tab()
    groups = api.tools(tab["id"])["groups"]
    assert "file" in groups and "terminal" in groups
    assert any(tool["name"] == "read_file" for tool in groups["file"])


def test_session_title_follows_the_first_message(config):
    api = Api(config)
    tab = api.new_tab()
    session = api.sessions[tab["id"]]
    assert session.title == "New session"

    session.busy = True  # stop send_message from actually starting a run
    session.send_message("this should not change the title while busy")
    assert session.title == "New session"
