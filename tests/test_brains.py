"""The two ways Vigil can think.

The point of these tests is that the picker's promises are kept: what the
description says about each brain is what the agent actually does. And that
neither of them touches security.
"""

import pytest

from vigil import brains
from vigil.agent import Agent
from vigil.config import Config
from vigil.providers.base import AssistantMessage, Provider
from vigil.security import ALWAYS_ASK, Guard, Risk
from vigil.tools import Registry, ToolSpec
from vigil.ui import UI


class _Provider(Provider):
    name = "fake"

    def __init__(self):
        self.model = "fake-model"
        self.total_tokens = 0
        self.request_count = 0

    def chat(self, messages, tools=None, on_text=None, model=None, temperature=None):
        return AssistantMessage(content="done")

    def vision(self, prompt, image_b64, model=None):
        return ""

    def list_models(self):
        return []


def _registry():
    registry = Registry()
    for name, group in (("echo", "core"), ("create_plan", "planning"),
                        ("update_plan", "planning")):
        registry.register(
            ToolSpec(
                name=name,
                description=name,
                parameters={"type": "object", "properties": {}},
                handler=lambda ctx, **kw: "",
                risk=Risk.SAFE,
                group=group,
            )
        )
    return registry


@pytest.fixture
def make_agent(tmp_path):
    def factory(brain="direct", max_steps=None):
        config = Config()
        config.api_key = "test"
        config.stream = False
        config.brain = brain
        if max_steps is not None:
            config.max_steps = max_steps
        guard = Guard(mode="auto", confirm=None, audit=False)
        return Agent(config, _Provider(), _registry(), guard,
                     UI(interactive=False, quiet=True), cwd=tmp_path)

    return factory


# ------------------------------------------------------------- the two of them
def test_there_are_exactly_two_and_direct_comes_first():
    assert brains.names() == ["direct", "autonomous"]


def test_an_unknown_name_falls_back_rather_than_failing():
    assert brains.get("galaxy-brain").key == brains.DEFAULT
    assert brains.get("").key == brains.DEFAULT
    assert brains.get(None).key == brains.DEFAULT


def test_the_name_is_read_however_it_is_typed():
    assert brains.get("  Autonomous ").key == "autonomous"


def test_the_safer_one_is_what_you_get_by_default():
    assert Config().brain == "direct"
    assert brains.DEFAULT == "direct"


def test_every_option_can_be_drawn():
    for described in brains.describe_all():
        for field in ("key", "name", "tagline", "summary", "model"):
            assert described[field]


def test_only_the_riskier_one_carries_a_warning():
    assert brains.get("direct").warning == ""
    assert brains.get("autonomous").warning


def test_the_warning_says_what_is_actually_riskier():
    """It acts more; it does not ask less. The distinction is the whole point."""
    warning = brains.get("autonomous").warning.lower()
    assert "more actions" in warning
    assert "still asks" in warning


# ------------------------------------------- the descriptions are kept honest
def test_direct_does_not_plan_and_autonomous_does():
    assert brains.get("direct").plans is False
    assert brains.get("autonomous").plans is True


def test_direct_is_given_a_shorter_leash():
    assert brains.get("direct").max_steps < brains.get("autonomous").max_steps


def test_the_planning_tools_are_not_even_offered_to_direct(make_agent):
    names = [tool["function"]["name"] for tool in make_agent("direct").tool_schemas]
    assert "echo" in names
    assert "create_plan" not in names


def test_autonomous_gets_the_planning_tools(make_agent):
    names = [tool["function"]["name"] for tool in make_agent("autonomous").tool_schemas]
    assert "create_plan" in names
    assert "update_plan" in names


def test_the_step_budget_follows_the_brain(make_agent):
    assert make_agent("direct").max_steps == 14
    assert make_agent("autonomous").max_steps == 40


def test_a_user_who_wants_fewer_steps_still_gets_fewer(make_agent):
    """The brain raises the ceiling it is given; it never lifts the user's."""
    assert make_agent("autonomous", max_steps=5).max_steps == 5


def test_each_brain_tells_the_model_how_to_work(make_agent):
    direct = make_agent("direct").messages[0]["content"]
    autonomous = make_agent("autonomous").messages[0]["content"]

    assert "APPROACH - DIRECT" in direct
    assert "Do not write a plan" in direct
    assert "APPROACH - AUTONOMOUS" in autonomous
    assert "create_plan" in autonomous


def test_switching_brain_changes_the_tools_and_the_prompt(make_agent):
    agent = make_agent("direct")
    agent.set_brain("autonomous")

    names = [tool["function"]["name"] for tool in agent.tool_schemas]
    assert "create_plan" in names
    assert "APPROACH - AUTONOMOUS" in agent.messages[0]["content"]
    assert agent.max_steps == 40


def test_switching_back_takes_the_planning_tools_away_again(make_agent):
    agent = make_agent("autonomous")
    agent.set_brain("direct")
    names = [tool["function"]["name"] for tool in agent.tool_schemas]
    assert "create_plan" not in names


# ---------------------------------------------------------------- security
def test_neither_brain_can_change_what_is_always_confirmed(make_agent):
    """The founding rule: how it thinks never changes what it may do."""
    for key in brains.names():
        agent = make_agent(key)
        assert agent.guard.mode == "auto"
        for tool in ("mouse_click", "keyboard_type", "screen_capture"):
            assert tool in ALWAYS_ASK


def test_a_brain_carries_no_security_settings():
    """Nothing about approval belongs in this module - it would be a way round it."""
    for key in brains.names():
        fields = brains.get(key).__dict__
        for name in fields:
            assert "approval" not in name
            assert "guard" not in name
            assert "risk" not in name


def test_both_brains_are_told_the_security_rules(make_agent):
    for key in brains.names():
        prompt = make_agent(key).messages[0]["content"]
        assert "SECURITY" in prompt
        assert "NEVER try to disable security protections" in prompt


# -------------------------------------------------------------- the setting
def test_the_setting_only_accepts_a_real_brain():
    config = Config()
    assert config.set_value("brain", "autonomous") == "autonomous"
    with pytest.raises(ValueError):
        config.set_value("brain", "clever")


# --------------------------------------------------------------- the bar's end
def _api(brain="direct", provider="groq"):
    from vigil.desktop.app import Api

    config = Config()
    config.api_key = "test"
    config.provider = provider
    config.brain = brain
    api = Api(config)
    api.new_tab()
    return api


def test_the_bar_offers_both_and_says_which_is_on():
    api = _api()
    reported = api.state()
    assert reported["brain"] == "direct"
    assert [b["key"] for b in reported["brains"]] == ["direct", "autonomous"]


def test_choosing_a_brain_changes_the_model_too():
    """It is presented as a model choice because it is one."""
    api = _api()
    result = api.set_brain("autonomous")

    assert result["brain"] == "autonomous"
    assert result["model"] == brains.get("autonomous").model
    session = next(iter(api.sessions.values()))
    assert session.agent.provider.model == brains.get("autonomous").model
    assert session.agent.max_steps == 40


def test_a_local_model_is_left_where_the_user_put_it():
    """Ollama users pulled their own model; a hosted model id would break it."""
    api = _api(provider="ollama")
    before = api.config.ollama_model
    api.set_brain("autonomous")

    assert api.config.ollama_model == before
    assert next(iter(api.sessions.values())).agent.brain.key == "autonomous"


def test_a_brain_that_does_not_exist_is_refused():
    api = _api()
    assert "error" in api.set_brain("wizard")
    assert api.config.brain == "direct"
