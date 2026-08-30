"""Provider layer tests - no network access required."""

from vigil.config import Config
from vigil.providers import GroqProvider, OllamaProvider, build_provider, provider_notes
from vigil.providers.base import ToolCall, to_openai_tools, tool_result_message
from vigil.providers.ollama_provider import _to_ollama_messages, _to_tool_call
from vigil.security import Risk
from vigil.tools import ToolSpec


# -------------------------------------------------------------- tool calls
def test_tool_call_parses_json_arguments():
    call = ToolCall.from_raw("id1", "run_command", '{"command": "ls -la"}')
    assert call.arguments == {"command": "ls -la"}
    assert call.raw_arguments == '{"command": "ls -la"}'


def test_tool_call_survives_broken_json():
    call = ToolCall.from_raw("id1", "run_command", "{broken json")
    assert call.arguments == {}
    assert call.name == "run_command"


def test_tool_call_with_empty_arguments():
    assert ToolCall.from_raw("id1", "system_info", "").arguments == {}


# ----------------------------------------------------------------- schemas
def test_tool_schema_conversion():
    spec = ToolSpec(
        name="example",
        description="An example tool.",
        parameters={"type": "object", "properties": {"x": {"type": "string"}}},
        handler=lambda ctx: "",
        risk=Risk.SAFE,
    )
    schema = to_openai_tools([spec])[0]
    assert schema["type"] == "function"
    assert schema["function"]["name"] == "example"
    assert schema["function"]["parameters"]["properties"]["x"]["type"] == "string"


def test_tool_result_message_serializes_non_strings():
    message = tool_result_message("call_1", "list_dir", {"files": 3})
    assert message["role"] == "tool"
    assert message["tool_call_id"] == "call_1"
    assert "files" in message["content"]


# ----------------------------------------------------------- model support
def test_groq_vision_support_detection():
    assert GroqProvider.supports_vision("qwen/qwen3.8-27b") is True
    assert GroqProvider.supports_vision("openai/gpt-oss-120b") is False


def test_groq_tool_support_detection():
    assert GroqProvider.supports_tools("openai/gpt-oss-120b") is True
    assert GroqProvider.supports_tools("groq/compound") is False


def test_provider_notes_warns_for_a_toolless_model():
    config = Config()
    config.model = "groq/compound"
    assert "does not support tool calling" in provider_notes(config)


def test_provider_notes_is_silent_for_a_good_model():
    config = Config()
    config.model = "openai/gpt-oss-120b"
    assert provider_notes(config) == ""


def test_build_provider_selects_ollama():
    config = Config()
    config.provider = "ollama"
    assert isinstance(build_provider(config), OllamaProvider)


# ------------------------------------------------------ ollama conversions
def test_ollama_message_conversion():
    messages = [
        {"role": "system", "content": "system"},
        {"role": "user", "content": "hello"},
        {
            "role": "assistant",
            "content": "",
            "tool_calls": [
                {"id": "c1", "type": "function",
                 "function": {"name": "run_command", "arguments": '{"command": "ls"}'}}
            ],
        },
        {"role": "tool", "tool_call_id": "c1", "name": "run_command", "content": "file1"},
    ]
    converted = _to_ollama_messages(messages)

    assert converted[0]["role"] == "system"
    # Ollama expects tool arguments as a dict, not a JSON string
    assert converted[2]["tool_calls"][0]["function"]["arguments"] == {"command": "ls"}
    # tool results are tagged with tool_name
    assert converted[3]["role"] == "tool"
    assert converted[3]["tool_name"] == "run_command"


def test_ollama_tool_call_parsing():
    call = _to_tool_call({"function": {"name": "list_dir", "arguments": {"path": "."}}}, 0)
    assert call.name == "list_dir"
    assert call.arguments == {"path": "."}
    assert call.id  # an id must have been generated


def test_ollama_vision_and_tool_hints():
    assert OllamaProvider.supports_vision("llama3.2-vision") is True
    assert OllamaProvider.supports_vision("qwen3:8b") is False
    assert OllamaProvider.supports_tools("qwen3:8b") is True


def test_ollama_connection_error_is_friendly():
    provider = OllamaProvider(host="http://localhost:59999", model="qwen3:8b")
    error = provider._translate(OSError("[Errno 111] Connection refused"), "")
    assert "Ollama" in str(error)
