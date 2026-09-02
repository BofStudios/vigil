"""Circling something on screen and asking about it.

No window is opened here. The picker's geometry is tested directly, the
subprocess handshake against a stubbed run, and the app wiring with the vision
model replaced - so this runs on a headless CI box like any other test.
"""

import json

import pytest

from vigil.config import Config
from vigil.desktop import overlay
from vigil.desktop.app import Api


@pytest.fixture
def config():
    settings = Config()
    settings.api_key = "not-a-real-key"
    return settings


# ------------------------------------------------------------- the geometry
def _selector(points, width=1920, height=1080):
    selector = overlay.Selector(width, height)
    selector._points = list(points)
    selector._finish()
    return selector


def test_a_stray_click_is_not_a_selection():
    assert _selector([(10, 10)]).cancelled is True


def test_a_scribble_too_small_to_mean_anything_is_ignored():
    assert _selector([(10, 10), (12, 11), (13, 12)]).cancelled is True


def test_a_swipe_along_a_line_of_text_still_counts():
    """Short in one direction on purpose - underlining a word is a real gesture."""
    selector = _selector([(100, 300), (180, 302), (260, 301), (340, 303)])
    assert selector.cancelled is False
    left, top, width, height = selector.region
    assert width > 240 and height >= 12


def test_a_circled_area_comes_back_as_the_box_around_it():
    selector = _selector([(100, 200), (300, 210), (280, 400), (110, 380)])
    left, top, width, height = selector.region

    # the box is the drawn extent plus a little breathing room
    assert left == 92 and top == 192
    assert width == 216 and height == 216
    assert selector.cancelled is False


def test_a_loop_drawn_off_the_edge_is_clamped_to_the_screen():
    selector = _selector([(2, 2), (400, 3), (399, 300), (1, 299)], width=1920, height=1080)
    left, top, width, height = selector.region
    assert left == 0 and top == 0
    assert left + width <= 1920
    assert top + height <= 1080


def test_the_box_never_runs_past_the_far_edge():
    selector = _selector([(1900, 1060), (1919, 1079), (1700, 900)], width=1920, height=1080)
    left, top, width, height = selector.region
    assert left + width <= 1920
    assert top + height <= 1080


# ------------------------------------------------------ the picker's process
class _Finished:
    def __init__(self, stdout):
        self.stdout = stdout
        self.returncode = 0


def _stub_run(monkeypatch, stdout):
    """Stand in for the picker process, which would otherwise want a screen."""
    import subprocess

    monkeypatch.setattr(subprocess, "run", lambda *a, **k: _Finished(stdout))


def test_the_picker_hands_back_what_was_circled(monkeypatch):
    _stub_run(monkeypatch, json.dumps({"region": [10, 20, 300, 200]}) + "\n")
    assert overlay.pick() == (10, 20, 300, 200)


def test_giving_up_on_the_picker_selects_nothing(monkeypatch):
    _stub_run(monkeypatch, json.dumps({"cancelled": True}) + "\n")
    assert overlay.pick() is None


def test_chatter_before_the_answer_is_stepped_over(monkeypatch):
    """Anything the toolkit prints on the way up must not be mistaken for output."""
    noise = "Xlib: some warning\n" + json.dumps({"region": [1, 2, 3, 4]}) + "\n"
    _stub_run(monkeypatch, noise)
    assert overlay.pick() == (1, 2, 3, 4)


def test_a_picker_that_says_nothing_selects_nothing(monkeypatch):
    _stub_run(monkeypatch, "")
    assert overlay.pick() is None


def test_a_picker_that_will_not_start_is_not_an_error(monkeypatch):
    import subprocess

    def explode(*_args, **_kwargs):
        raise OSError("no display")

    monkeypatch.setattr(subprocess, "run", explode)
    assert overlay.pick() is None


def test_a_broken_answer_selects_nothing(monkeypatch):
    _stub_run(monkeypatch, "{not json at all\n")
    assert overlay.pick() is None


# ------------------------------------------------------------- the app wiring
def _api(config):
    api = Api(config)
    api.new_tab()
    api.show_window = lambda: None
    return api


def test_what_was_circled_becomes_a_question(config, monkeypatch):
    api = _api(config)
    sent = []
    monkeypatch.setattr(overlay, "pick", lambda: (10, 10, 200, 100))
    monkeypatch.setattr(Api, "_describe", lambda self, region: "a red error dialog")
    monkeypatch.setattr(api, "send", lambda tab, text: sent.append(text))

    api._ask_screen()

    assert len(sent) == 1
    assert "a red error dialog" in sent[0]
    assert "web" in sent[0].lower()      # it is allowed to go and look it up


def test_giving_up_asks_nothing(config, monkeypatch):
    api = _api(config)
    sent = []
    monkeypatch.setattr(overlay, "pick", lambda: None)
    monkeypatch.setattr(api, "send", lambda tab, text: sent.append(text))

    api._ask_screen()
    assert sent == []


def test_the_picker_is_released_even_when_looking_fails(config, monkeypatch):
    api = _api(config)
    monkeypatch.setattr(overlay, "pick", lambda: (0, 0, 100, 100))

    def fail(self, region):
        raise RuntimeError("vision model is down")

    monkeypatch.setattr(Api, "_describe", fail)
    events = []
    api.emit = events.append

    api._ask_screen()

    assert api.asking is False      # otherwise the hot key would be dead for good
    assert events[-1]["state"] == "error"
    assert "vision model is down" in events[-1]["text"]


def test_only_one_picker_at_a_time(config):
    api = _api(config)
    api.asking = True
    assert api.ask_screen()["ok"] is False


def test_the_state_reports_whether_circling_is_available(config):
    api = _api(config)
    assert api.state()["ask_screen"] is False   # no hot key registered in tests


def test_looking_says_so_before_it_asks(config, monkeypatch):
    """The bar should not sit silent while the vision model thinks."""
    api = _api(config)
    monkeypatch.setattr(overlay, "pick", lambda: (0, 0, 100, 100))
    monkeypatch.setattr(Api, "_describe", lambda self, region: "a chart")
    monkeypatch.setattr(api, "send", lambda tab, text: None)
    events = []
    api.emit = events.append

    api._ask_screen()

    thinking = [e for e in events if e.get("state") == "thinking"]
    assert thinking and thinking[0]["label"] == "Looking"
