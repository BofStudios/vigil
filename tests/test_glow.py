"""The screen glow, and the light that decides when it is on.

Nothing here opens a real window. The drawing is plain PIL and runs anywhere;
the timing logic is exercised against a stand-in overlay so it is tested on
Linux CI as well as on Windows.
"""

import pytest

from vigil.desktop import glow as glow_module
from vigil.desktop.glow import ControlLight, Glow, _alpha_at, _premultiplied, build_glow

PIL = pytest.importorskip("PIL")


# --------------------------------------------------------------- the drawing
def test_the_light_is_brightest_at_the_very_edge():
    assert _alpha_at(0) == glow_module.CORE_ALPHA
    assert _alpha_at(1) == glow_module.CORE_ALPHA


def test_it_fades_the_further_in_it_goes():
    values = [_alpha_at(d) for d in range(2, glow_module.REACH)]
    assert values == sorted(values, reverse=True)
    assert values[0] > values[-1] * 8   # a real falloff, not a flat band


def test_it_is_light_rather_than_a_grey_band():
    """The failure this replaced: an even wash across a wide band.

    Light is a bright edge that dies quickly. Halfway to the reach it should be
    a fraction of the edge, not most of it.
    """
    edge = _alpha_at(0)
    halfway = _alpha_at(glow_module.REACH // 2)
    assert halfway < edge * 0.15


def test_the_bloom_has_run_out_by_the_time_it_reaches_its_limit():
    assert _alpha_at(glow_module.REACH) < 0.02


def test_the_glow_frames_the_screen_and_leaves_the_middle_alone():
    image = build_glow(400, 300)
    alpha = image.split()[3]

    assert alpha.getpixel((200, 150)) == 0          # the middle is untouched
    assert alpha.getpixel((1, 150)) > 200           # a bright line down the side
    assert alpha.getpixel((200, 1)) > 200           # and across the top


def test_the_corners_are_rounded_not_square():
    """A square distance-to-edge leaves a diagonal seam; the radius removes it."""
    alpha = build_glow(400, 300).split()[3]
    assert alpha.getpixel((0, 0)) < alpha.getpixel((0, 150))


def test_a_screen_narrower_than_the_reach_still_draws():
    image = build_glow(20, 20)
    assert image.size == (20, 20)


def test_the_buffer_is_premultiplied_bgra():
    from PIL import Image

    red = Image.new("RGBA", (2, 1), (255, 0, 0, 255))
    assert _premultiplied(red)[:4] == b"\x00\x00\xff\xff"

    half = Image.new("RGBA", (1, 1), (255, 255, 255, 128))
    assert _premultiplied(half) == b"\x80\x80\x80\x80"


def test_the_buffer_is_the_size_the_bitmap_expects():
    assert len(_premultiplied(build_glow(64, 48))) == 64 * 48 * 4


# ------------------------------------------------------------- when it is on
def test_capture_tools_never_light_it():
    """The glow must not end up in the screenshot the model is shown."""
    assert "screen_capture" not in ControlLight.TOOLS
    assert "screen_size" not in ControlLight.TOOLS


def test_the_tools_that_do_light_it_are_the_ones_driving_input():
    for name in ("mouse_click", "mouse_move", "keyboard_type", "press_keys"):
        assert name in ControlLight.TOOLS


class _FakeGlow:
    """Stands in for the layered window so the timing can be tested anywhere."""

    def __init__(self, *_args):
        self.showing = False
        self.shows = 0
        self.error = ""

    def prepare(self, timeout=8.0):
        return True

    def show(self):
        self.showing = True
        self.shows += 1
        return True

    def hide(self):
        self.showing = False

    def close(self):
        self.showing = False


@pytest.fixture
def light(monkeypatch):
    """A control light wired to a fake overlay, on any platform."""
    monkeypatch.setattr(glow_module, "IS_WINDOWS", True)
    monkeypatch.setattr(glow_module, "Glow", _FakeGlow)
    monkeypatch.setattr(glow_module.ControlLight, "LINGER", 0.25)
    controller = ControlLight()
    yield controller
    controller.stop()


def test_touching_it_turns_the_light_on(light):
    light.touch()
    assert light._glow.showing is True


def test_a_burst_of_actions_is_one_continuous_light(light):
    for _ in range(5):
        light.touch()
    assert light._glow.shows == 1   # not five separate flashes


def test_it_goes_out_once_the_actions_stop(light):
    import time

    light.touch()
    time.sleep(0.6)
    assert light._glow.showing is False


def test_a_screenshot_is_taken_in_the_dark(light):
    light.touch()
    with light.suspend():
        assert light._glow.showing is False
    assert light._glow.showing is True   # and comes back afterwards


def test_a_screenshot_after_the_light_went_out_leaves_it_out(light):
    import time

    light.touch()
    time.sleep(0.6)
    with light.suspend():
        pass
    assert light._glow.showing is False


def test_nested_suspends_do_not_switch_it_back_on_too_early(light):
    light.touch()
    with light.suspend():
        with light.suspend():
            pass
        assert light._glow.showing is False
    assert light._glow.showing is True


def test_once_stopped_it_stays_off(light):
    light.stop()
    light.touch()
    assert light._glow is None


def test_it_does_nothing_at_all_off_windows(monkeypatch):
    monkeypatch.setattr(glow_module, "IS_WINDOWS", False)
    controller = ControlLight()
    controller.prepare()
    controller.touch()          # must not raise, must not build anything
    assert controller._glow is None
    with controller.suspend():
        pass


def test_the_overlay_refuses_to_build_off_windows(monkeypatch):
    monkeypatch.setattr(glow_module, "IS_WINDOWS", False)
    assert Glow(800, 600).prepare() is False
    assert Glow(800, 600).show() is False


# ----------------------------------------------------------- the agent's end
def test_the_agent_lights_up_only_for_control_tools(monkeypatch):
    from vigil import agent as agent_module

    touched = []
    monkeypatch.setattr(glow_module, "IS_WINDOWS", True)
    monkeypatch.setattr(glow_module, "_LIGHT", None)
    monkeypatch.setattr(glow_module, "Glow", _FakeGlow)

    controller = glow_module.light()
    monkeypatch.setattr(controller, "touch", lambda: touched.append(True))

    agent_module._light_up("read_file")
    assert touched == []

    agent_module._light_up("mouse_click")
    assert touched == [True]

    glow_module._LIGHT = None


def test_lighting_up_never_breaks_a_tool_call(monkeypatch):
    """A broken overlay must not stop Vigil from doing the work."""
    from vigil import agent as agent_module

    monkeypatch.setattr(glow_module, "_LIGHT", None)
    monkeypatch.setattr(
        glow_module, "light", lambda: (_ for _ in ()).throw(RuntimeError("no display"))
    )
    agent_module._light_up("mouse_click")   # swallowed, nothing raised
