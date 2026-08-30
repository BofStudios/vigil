"""Agent loop tests, driven by a fake provider."""

import pytest

from vigil.agent import Agent
from vigil.config import Config
from vigil.providers.base import AssistantMessage, Provider, ToolCall
from vigil.security import Guard, Risk
from vigil.tools import Registry, ToolSpec
from vigil.ui import UI


class FakeProvider(Provider):
    """Returns a scripted list of responses, one per call."""

    name = "fake"

    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []
        self.total_tokens = 0
        self.request_count = 0

    def chat(self, messages, tools=None, on_text=None, model=None, temperature=None):
        self.calls.append(list(messages))
        self.request_count += 1
        if not self.responses:
            return AssistantMessage(content="done")
        return self.responses.pop(0)

    def vision(self, prompt, image_b64, model=None):
        return "fake image analysis"

    def list_models(self):
        return ["fake-model"]


def _echo(ctx, text="", **kwargs):
    return "echo: " + text


def _boom(ctx, **kwargs):
    raise RuntimeError("kaboom")


def _registry():
    registry = Registry()
    registry.register(
        ToolSpec(
            name="echo",
            description="Echoes the given text.",
            parameters={"type": "object", "properties": {"text": {"type": "string"}}},
            handler=_echo,
            risk=Risk.SAFE,
        )
    )
    registry.register(
        ToolSpec(
            name="boom",
            description="Raises an error.",
            parameters={"type": "object", "properties": {}},
            handler=_boom,
            risk=Risk.SAFE,
        )
    )
    return registry


@pytest.fixture
def make_agent(tmp_path):
    def factory(responses, mode="yolo"):
        config = Config()
        config.api_key = "test"
        config.stream = False
        guard = Guard(mode=mode, confirm=None, audit=False)
        ui = UI(interactive=False, quiet=True)
        return Agent(config, FakeProvider(responses), _registry(), guard, ui, cwd=tmp_path)

    return factory


def test_plain_answer_without_tools(make_agent):
    assert make_agent([AssistantMessage(content="hello")]).run("hi") == "hello"


def test_tool_call_then_answer(make_agent):
    agent = make_agent(
        [
            AssistantMessage(tool_calls=[ToolCall.from_raw("1", "echo", '{"text": "vigil"}')]),
            AssistantMessage(content="finished"),
        ]
    )
    assert agent.run("run echo") == "finished"
    tool_messages = [m for m in agent.messages if m.get("role") == "tool"]
    assert tool_messages and "echo: vigil" in tool_messages[0]["content"]


def test_unknown_tool_is_reported_to_the_model(make_agent):
    agent = make_agent(
        [
            AssistantMessage(tool_calls=[ToolCall.from_raw("1", "missing_tool", "{}")]),
            AssistantMessage(content="ok"),
        ]
    )
    agent.run("try it")
    tool_messages = [m for m in agent.messages if m.get("role") == "tool"]
    assert "no tool called" in tool_messages[0]["content"]


def test_tool_exception_is_fed_back(make_agent):
    agent = make_agent(
        [
            AssistantMessage(tool_calls=[ToolCall.from_raw("1", "boom", "{}")]),
            AssistantMessage(content="saw the error"),
        ]
    )
    agent.run("break it")
    tool_messages = [m for m in agent.messages if m.get("role") == "tool"]
    assert "UNEXPECTED ERROR" in tool_messages[0]["content"]


def test_unknown_arguments_are_dropped(make_agent):
    agent = make_agent(
        [
            AssistantMessage(tool_calls=[ToolCall.from_raw("1", "echo", '{"text": "a", "invented": 5}')]),
            AssistantMessage(content="ok"),
        ]
    )
    agent.run("try it")
    tool_messages = [m for m in agent.messages if m.get("role") == "tool"]
    assert "echo: a" in tool_messages[0]["content"]


def test_step_limit_is_enforced(make_agent):
    responses = [
        AssistantMessage(tool_calls=[ToolCall.from_raw(str(i), "echo", '{"text": "x"}')]) for i in range(10)
    ]
    agent = make_agent(responses)
    agent.config.max_steps = 3
    agent.run("infinite loop")
    assert agent.steps_used == 3


def test_history_trim_keeps_the_system_message(make_agent):
    agent = make_agent([AssistantMessage(content="ok")])
    agent.config.max_history_messages = 8
    for index in range(40):
        agent.messages.append({"role": "user", "content": "message " + str(index)})
    agent._trim()
    assert agent.messages[0]["role"] == "system"
    assert len(agent.messages) <= 9


def test_session_save_and_load(make_agent):
    agent = make_agent([AssistantMessage(content="hello")])
    agent.run("hi")
    path = agent.save_session("test_session")
    assert path.exists()

    agent.reset()
    assert len(agent.messages) == 1
    assert agent.load_session(path) >= 3
    path.unlink()


# ------------------------------------------------------- cli argument parsing
def test_cli_flag_values_are_not_mistaken_for_subcommands():
    from vigil.cli import _first_positional_index

    # "auto" is the value of --mode, not a subcommand
    assert _first_positional_index(["--mode", "auto", "list the files"]) == 2
    assert _first_positional_index(["--quiet", "doctor"]) == 1
    assert _first_positional_index(["--yolo", "--no-stream"]) is None
    assert _first_positional_index(["hello"]) == 0
