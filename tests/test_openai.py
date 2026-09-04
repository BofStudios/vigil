"""OpenAI, and anything that speaks its API.

Nothing here reaches a network. The part worth testing is the streaming
reassembly: tool calls arrive in fragments keyed by index, and a name or an
argument dropped on the floor is an agent that silently does nothing.
"""

import pytest

from vigil.config import Config
from vigil.providers import build_provider
from vigil.providers.base import AuthError, ProviderError, RateLimitError
from vigil.providers.openai_provider import (
    DEFAULT_MODEL,
    KNOWN_MODELS,
    OpenAIProvider,
)


class _Piece:
    """One fragment of a tool call, the shape the SDK yields."""

    def __init__(self, index, call_id=None, name=None, arguments=None):
        self.index = index
        self.id = call_id
        self.function = type("F", (), {"name": name, "arguments": arguments})()


class _Delta:
    def __init__(self, content=None, tool_calls=None):
        self.content = content
        self.tool_calls = tool_calls


class _Chunk:
    def __init__(self, content=None, tool_calls=None, finish=None, usage=None):
        self.usage = usage
        self.choices = [type("C", (), {
            "delta": _Delta(content, tool_calls),
            "finish_reason": finish,
        })()]


def _provider(monkeypatch, stream_chunks=None):
    """A provider with the SDK replaced by something that yields fragments."""
    pytest.importorskip("openai")
    made = OpenAIProvider.__new__(OpenAIProvider)
    made.model = DEFAULT_MODEL
    made.vision_model = DEFAULT_MODEL
    made.base_url = ""
    made.temperature = 0.3
    made.total_tokens = 0
    made.request_count = 0

    class _Completions:
        def create(self, **kwargs):
            return iter(stream_chunks or [])

    made._client = type("Client", (), {
        "chat": type("Chat", (), {"completions": _Completions()})(),
    })()
    return made


# ------------------------------------------------------------- streaming
def test_streamed_text_arrives_in_order_and_is_handed_over_as_it_comes(monkeypatch):
    seen = []
    provider = _provider(monkeypatch, [
        _Chunk(content="Open"), _Chunk(content="ing "), _Chunk(content="it."),
    ])

    answer = provider.chat([{"role": "user", "content": "hi"}], on_text=seen.append)

    assert seen == ["Open", "ing ", "it."]
    assert answer.content == "Opening it."


def test_a_tool_call_split_across_chunks_is_put_back_together(monkeypatch):
    """The name comes in one fragment and the arguments in several others."""
    provider = _provider(monkeypatch, [
        _Chunk(tool_calls=[_Piece(0, call_id="call_1", name="open_url")]),
        _Chunk(tool_calls=[_Piece(0, arguments='{"url":')]),
        _Chunk(tool_calls=[_Piece(0, arguments='"a.com"}')]),
        _Chunk(finish="tool_calls"),
    ])

    answer = provider.chat([{"role": "user", "content": "hi"}], on_text=lambda _t: None)

    assert len(answer.tool_calls) == 1
    call = answer.tool_calls[0]
    assert call.id == "call_1"
    assert call.name == "open_url"
    assert call.arguments == {"url": "a.com"}
    assert answer.finish_reason == "tool_calls"


def test_two_tool_calls_at_once_stay_apart(monkeypatch):
    provider = _provider(monkeypatch, [
        _Chunk(tool_calls=[_Piece(0, call_id="a", name="one", arguments="{}")]),
        _Chunk(tool_calls=[_Piece(1, call_id="b", name="two", arguments='{"x":1}')]),
    ])

    answer = provider.chat([], on_text=lambda _t: None)

    assert [call.name for call in answer.tool_calls] == ["one", "two"]
    assert answer.tool_calls[1].arguments == {"x": 1}


def test_a_fragment_that_never_names_a_tool_is_dropped(monkeypatch):
    """Otherwise it becomes a call to a tool with no name, which is an error."""
    provider = _provider(monkeypatch, [
        _Chunk(tool_calls=[_Piece(0, arguments="{}")]),
    ])
    assert provider.chat([], on_text=lambda _t: None).tool_calls == []


def test_a_chunk_with_no_choices_is_skipped(monkeypatch):
    """The final usage-only chunk has none, and used to raise IndexError."""
    usage = type("U", (), {"total_tokens": 42, "prompt_tokens": 30,
                           "completion_tokens": 12})()
    empty = _Chunk(content="hi")
    empty.choices = []
    empty.usage = usage

    provider = _provider(monkeypatch, [_Chunk(content="hi"), empty])
    answer = provider.chat([], on_text=lambda _t: None)

    assert answer.content == "hi"
    assert provider.total_tokens == 42


# ----------------------------------------------------------- anything else
def test_a_compatible_endpoint_is_just_a_base_url():
    """OpenRouter, a local vLLM, LM Studio - the same shape, so the same code."""
    settings = Config()
    settings.provider = "openai"
    settings.openai_api_key = "sk-test"
    settings.openai_base_url = "https://openrouter.ai/api/v1"

    pytest.importorskip("openai")
    built = build_provider(settings)
    assert built.base_url == "https://openrouter.ai/api/v1"


def test_openai_is_the_default_endpoint_when_none_is_given():
    settings = Config()
    settings.provider = "openai"
    settings.openai_api_key = "sk-test"

    pytest.importorskip("openai")
    assert build_provider(settings).base_url == ""


def test_the_config_keeps_this_key_apart_from_the_others():
    settings = Config()
    settings.api_key = "gsk_free"
    settings.anthropic_api_key = "sk-ant"
    settings.openai_api_key = "sk-proj"
    settings.provider = "openai"

    assert settings.active_model == DEFAULT_MODEL
    assert settings.active_vision_model == DEFAULT_MODEL


def test_changing_the_model_lands_on_the_right_provider():
    settings = Config()
    settings.provider = "openai"
    settings.set_active_model("gpt-5.2-mini")

    assert settings.openai_model == "gpt-5.2-mini"
    assert settings.model != "gpt-5.2-mini"
    assert settings.anthropic_model != "gpt-5.2-mini"


def test_no_key_is_refused_with_somewhere_to_get_one():
    with pytest.raises(AuthError) as raised:
        OpenAIProvider(api_key="")
    assert "platform.openai.com" in str(raised.value)


def test_the_models_worth_offering_all_call_tools():
    for model in KNOWN_MODELS:
        assert OpenAIProvider.supports_tools(model)


# ------------------------------------------------------------------ errors
def test_a_refused_key_is_explained_in_words():
    problem = OpenAIProvider._translate(Exception("401 Incorrect API key provided"))
    assert isinstance(problem, AuthError)


def test_running_out_of_credit_points_at_the_free_option():
    problem = OpenAIProvider._translate(Exception("You exceeded your current quota"))
    assert "free provider" in str(problem)


def test_a_rate_limit_says_to_wait():
    assert isinstance(
        OpenAIProvider._translate(Exception("429 rate limit")), RateLimitError
    )


def test_a_model_you_cannot_reach_yet_says_so_plainly():
    """New releases are limited at first; "does not exist" is misleading."""
    problem = OpenAIProvider._translate(
        Exception("The model `gpt-6-astra` does not exist")
    )
    assert isinstance(problem, ProviderError)
    assert "not available to this key yet" in str(problem)
