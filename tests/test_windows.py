"""The Windows-facing pieces: starting with the machine, one instance, tray state.

None of these touch the real Startup folder, open a real window, or need a tray.
The platform-specific calls are behind `IS_WINDOWS` and are expected to decline
politely everywhere else, which is what most of this checks.
"""

import pytest

from vigil.config import Config
from vigil.desktop import shortcut, single
from vigil.desktop.app import Api
from vigil.desktop.tray import STATES, Tray


@pytest.fixture
def config():
    settings = Config()
    settings.api_key = "not-a-real-key"
    return settings


# ------------------------------------------------------ start with the machine
@pytest.fixture
def sandboxed_autostart(tmp_path, monkeypatch):
    """Point the startup entry at a temp folder - never the user's real one."""
    entry = tmp_path / "Vigil.lnk"
    monkeypatch.setattr(shortcut, "_autostart_path", lambda: entry)
    return entry


def test_it_does_not_start_with_the_machine_unless_asked(sandboxed_autostart):
    assert shortcut.autostart_enabled() is False


def test_turning_it_on_and_off_again(sandboxed_autostart, monkeypatch):
    monkeypatch.setattr(shortcut, "_create_windows", lambda path: _touch(path))
    monkeypatch.setattr(shortcut, "_create_linux", lambda path: _touch(path))
    monkeypatch.setattr(shortcut, "_create_launch_agent", lambda path: _touch(path))

    shortcut.enable_autostart()
    assert shortcut.autostart_enabled() is True

    assert shortcut.disable_autostart() is True
    assert shortcut.autostart_enabled() is False


def test_turning_it_off_when_it_was_never_on_is_not_an_error(sandboxed_autostart):
    assert shortcut.disable_autostart() is False


def test_a_system_with_nowhere_to_put_it_says_so(monkeypatch):
    monkeypatch.setattr(shortcut, "_autostart_path", lambda: None)
    assert shortcut.autostart_enabled() is False
    assert shortcut.disable_autostart() is False
    with pytest.raises(OSError):
        shortcut.enable_autostart()


def _touch(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("", encoding="utf-8")
    return path


def test_the_startup_folder_is_a_real_place_or_nothing():
    where = shortcut.autostart_dir()
    assert where is None or str(where)


# ------------------------------------------------------------- one at a time
def test_nothing_is_running_before_anything_starts(monkeypatch):
    monkeypatch.setattr(single, "IS_WINDOWS", False)
    assert single.find_running() is None
    assert single.summon() is False


def test_the_beacon_declines_politely_off_windows(monkeypatch):
    monkeypatch.setattr(single, "IS_WINDOWS", False)
    beacon = single.Beacon()
    assert beacon.start() is False
    beacon.stop()               # must not raise either


def test_the_summon_message_is_in_the_application_range():
    """Below WM_APP the number would collide with a message Windows defines."""
    assert single.WM_SUMMON >= single.WM_APP


@pytest.mark.skipif(not single.IS_WINDOWS, reason="needs a real message window")
def test_a_second_instance_finds_the_first_and_wakes_it():
    woken = []
    beacon = single.Beacon(on_summon=lambda: woken.append(True))
    assert beacon.start() is True
    try:
        assert single.find_running() is not None
        assert single.summon() is True
        for _ in range(40):
            if woken:
                break
            import time

            time.sleep(0.05)
        assert woken == [True]
    finally:
        beacon.stop()


# ------------------------------------------------------------------ the tray
def test_the_tray_knows_three_states():
    assert set(STATES) == {"idle", "busy", "waiting"}


def test_idle_carries_no_badge():
    """A badge that is always there stops meaning anything."""
    assert STATES["idle"][0] is None
    assert STATES["busy"][0] is not None
    assert STATES["waiting"][0] is not None


def test_each_state_says_what_it_means_on_hover():
    assert "working" in STATES["busy"][1]
    assert "waiting" in STATES["waiting"][1]


def test_an_unknown_state_is_ignored():
    tray = Tray()
    tray.set_state("on fire")
    assert tray.state == "idle"


def test_the_state_is_remembered_even_with_no_icon_running():
    tray = Tray()
    tray.set_state("busy")
    assert tray.state == "busy"


def test_the_badge_is_drawn_once_per_state():
    pytest.importorskip("PIL")
    from PIL import Image

    tray = Tray()
    tray._base = Image.new("RGBA", (64, 64), (0, 0, 0, 0))

    first = tray._image_for("busy")
    assert tray._image_for("busy") is first        # cached, not redrawn
    assert tray._image_for("waiting") is not first


def test_the_badge_lands_in_a_corner_and_leaves_the_mark_alone():
    pytest.importorskip("PIL")
    from PIL import Image

    tray = Tray()
    tray._base = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    drawn = tray._image_for("busy").load()

    assert drawn[58, 58][3] > 0     # bottom-right has the dot
    assert drawn[6, 6][3] == 0      # top-left is untouched


def test_the_autostart_item_is_hidden_when_there_is_nothing_to_toggle():
    tray = Tray()
    assert tray.on_autostart is None
    tray._toggle_autostart()        # must be a no-op, not a crash


# -------------------------------------------------------- the bar's own state
def test_an_approval_puts_the_tray_into_waiting(config):
    api = Api(config)
    api.new_tab()
    api.emit({"type": "approval", "request": "req-1", "tool": "delete_path"})
    assert api._waiting_on == {"req-1"}


def test_answering_it_clears_the_waiting_state(config):
    api = Api(config)
    tab = api.new_tab()["id"]
    api.emit({"type": "approval", "request": "req-1"})
    api.answer(tab, "req-1", "yes")
    assert api._waiting_on == set()


def test_a_finished_run_clears_anything_still_outstanding(config):
    """A run that ends without an answer must not leave the tray stuck."""
    api = Api(config)
    api.new_tab()
    api.emit({"type": "approval", "request": "req-1"})
    api.emit({"type": "status", "busy": False})
    assert api._waiting_on == set()


def test_the_tray_follows_the_work(config):
    api = Api(config)
    api.new_tab()

    seen = []

    class _Tray:
        available = True

        def set_state(self, state):
            seen.append(state)

    api.tray = _Tray()

    api.refresh_tray()
    assert seen[-1] == "idle"

    next(iter(api.sessions.values())).busy = True
    api.refresh_tray()
    assert seen[-1] == "busy"

    api.emit({"type": "approval", "request": "req-9"})
    assert seen[-1] == "waiting"     # an unanswered question outranks working


def test_the_state_reports_whether_it_starts_with_the_machine(config, monkeypatch):
    monkeypatch.setattr(shortcut, "_autostart_path", lambda: None)
    api = Api(config)
    api.new_tab()
    assert api.state()["autostart"] is False


def test_a_startup_folder_that_will_not_take_it_is_reported(config, monkeypatch):
    def refuse():
        raise OSError("Could not find this system's startup folder.")

    monkeypatch.setattr(shortcut, "enable_autostart", refuse)
    api = Api(config)
    assert "error" in api.set_autostart(True)
