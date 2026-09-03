"""Bringing your own Claude.

Nothing here reaches Anthropic. What is worth testing is the translation:
Vigil keeps its history in OpenAI's shape because two of the three providers
speak it, and Claude's Messages API is a different shape entirely. Getting that
wrong is the difference between an agent that works and one that silently
forgets it called a tool.
"""

import pytest

from vigil.config import Config
from vigil.providers import build_provider
from vigil.providers.anthropic_provider import (
    KNOWN_MODELS,
    AnthropicProvider,
    to_anthropic,
    to_anthropic_tools,
)
from vigil.providers.base import AuthError

HISTORY = [
    {"role": "system", "content": "You are Vigil."},
    {"role": "user", "content": "open youtube"},
    {"role": "assistant", "content": "On it.", "tool_calls": [
        {"id": "c1", "type": "function",
         "function": {"name": "open_url", "arguments": '{"url": "youtube.com"}'}},
    ]},
    {"role": "tool", "tool_call_id": "c1", "name": "open_url", "content": "opened"},
    {"role": "assistant", "content": "Done."},
]


# ------------------------------------------------------------- translation
def test_the_system_prompt_becomes_a_parameter_not_a_message():
    """Claude takes it alongside the messages, not inside them."""
    system, messages = to_anthropic(HISTORY)

    assert system == "You are Vigil."
    assert all(message["role"] != "system" for message in messages)


def test_several_system_messages_are_joined_rather_than_lost():
    system, _ = to_anthropic([
        {"role": "system", "content": "first"},
        {"role": "system", "content": "second"},
        {"role": "user", "content": "hi"},
    ])
    assert system == "first\n\nsecond"


def test_a_tool_call_becomes_a_block_inside_the_assistant_turn():
    _, messages = to_anthropic(HISTORY)
    assistant = messages[1]

    assert assistant["role"] == "assistant"
    kinds = [block["type"] for block in assistant["content"]]
    assert kinds == ["text", "tool_use"]

    call = assistant["content"][1]
    assert call["id"] == "c1"
    assert call["name"] == "open_url"
    assert call["input"] == {"url": "youtube.com"}


def test_a_tool_result_comes_back_as_a_user_turn():
    """Claude has no tool role; results are user content answering the call."""
    _, messages = to_anthropic(HISTORY)
    result = messages[2]

    assert result["role"] == "user"
    assert result["content"][0]["type"] == "tool_result"
    assert result["content"][0]["tool_use_id"] == "c1"


def test_a_run_of_tool_results_becomes_one_message():
    """Claude rejects a tool_result that is not answering the turn before it."""
    _, messages = to_anthropic([
        {"role": "user", "content": "do two things"},
        {"role": "assistant", "content": "", "tool_calls": [
            {"id": "a", "type": "function", "function": {"name": "one", "arguments": "{}"}},
            {"id": "b", "type": "function", "function": {"name": "two", "arguments": "{}"}},
        ]},
        {"role": "tool", "tool_call_id": "a", "name": "one", "content": "first"},
        {"role": "tool", "tool_call_id": "b", "name": "two", "content": "second"},
    ])

    results = [m for m in messages if m["role"] == "user"
               and m["content"][0]["type"] == "tool_result"]
    assert len(results) == 1
    assert [block["tool_use_id"] for block in results[0]["content"]] == ["a", "b"]


def test_arguments_that_are_not_json_do_not_bring_the_turn_down():
    _, messages = to_anthropic([
        {"role": "assistant", "content": "", "tool_calls": [
            {"id": "x", "type": "function",
             "function": {"name": "broken", "arguments": "{not json"}},
        ]},
    ])
    assert messages[0]["content"][0]["input"] == {}


def test_an_assistant_turn_with_nothing_in_it_is_dropped():
    """An empty message is rejected outright by the API."""
    _, messages = to_anthropic([
        {"role": "user", "content": "hi"},
        {"role": "assistant", "content": ""},
    ])
    assert len(messages) == 1


def test_the_last_tool_results_are_not_left_behind():
    """They are held back to be merged; the end of the list has to flush them."""
    _, messages = to_anthropic([
        {"role": "assistant", "content": "", "tool_calls": [
            {"id": "z", "type": "function", "function": {"name": "t", "arguments": "{}"}},
        ]},
        {"role": "tool", "tool_call_id": "z", "name": "t", "content": "done"},
    ])
    assert messages[-1]["content"][0]["type"] == "tool_result"


def test_tools_are_described_the_way_claude_wants():
    converted = to_anthropic_tools([{
        "type": "function",
        "function": {"name": "read_file", "description": "Reads a file.",
                     "parameters": {"type": "object", "properties": {"path": {}}}},
    }])

    assert converted == [{
        "name": "read_file",
        "description": "Reads a file.",
        "input_schema": {"type": "object", "properties": {"path": {}}},
    }]


def test_a_tool_with_no_schema_still_gets_a_valid_one():
    converted = to_anthropic_tools([{"type": "function", "function": {"name": "x"}}])
    assert converted[0]["input_schema"] == {"type": "object", "properties": {}}


# ------------------------------------------------------------------ setup
def test_no_key_is_refused_with_somewhere_to_get_one():
    with pytest.raises(AuthError) as raised:
        AnthropicProvider(api_key="")
    assert "console.anthropic.com" in str(raised.value)


def test_every_claude_model_can_see_and_call_tools():
    for model in KNOWN_MODELS:
        assert AnthropicProvider.supports_tools(model)
        assert AnthropicProvider.supports_vision(model)


def test_the_config_keeps_the_claude_key_apart_from_the_free_one():
    settings = Config()
    settings.api_key = "gsk_free"
    settings.anthropic_api_key = "sk-ant-paid"
    settings.provider = "anthropic"

    assert settings.active_model == "claude-sonnet-5"
    # Claude is multimodal, so the same model does the looking
    assert settings.active_vision_model == settings.active_model


def test_changing_the_model_lands_on_the_right_provider():
    settings = Config()
    settings.provider = "anthropic"
    settings.set_active_model("claude-opus-5")

    assert settings.anthropic_model == "claude-opus-5"
    assert settings.model != "claude-opus-5"      # the Groq one is untouched


def test_the_factory_builds_it(monkeypatch):
    pytest.importorskip("anthropic")

    settings = Config()
    settings.provider = "anthropic"
    settings.anthropic_api_key = "sk-ant-test"

    built = build_provider(settings)
    assert built.name == "anthropic"
    assert built.model == "claude-sonnet-5"


# ------------------------------------------------------------------ errors
def test_a_refused_key_is_explained_in_words():
    problem = AnthropicProvider._translate(Exception("authentication_error: invalid x-api-key"))
    assert isinstance(problem, AuthError)
    assert "did not accept" in str(problem)


def test_running_out_of_credit_points_at_the_free_option():
    problem = AnthropicProvider._translate(Exception("Your credit balance is too low"))
    assert "free provider" in str(problem)


def test_a_rate_limit_says_to_wait():
    problem = AnthropicProvider._translate(Exception("429 rate limit exceeded"))
    assert "Wait" in str(problem)
