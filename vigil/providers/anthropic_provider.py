"""Anthropic provider - bring your own Claude.

Not free, unlike Groq and Ollama, but it is what people who already pay for a
Claude key want to point at their own machine.

The conversation is kept in OpenAI's shape everywhere else in Vigil, because
that is what two of the three providers speak. Claude's Messages API is a
different shape - the system prompt is a parameter rather than a message, tool
calls are content blocks rather than a separate field, and tool results come
back as user messages - so this module translates in both directions and keeps
that translation in one place.

Get a key: https://console.anthropic.com/settings/keys
"""

from __future__ import annotations

import json
from typing import Callable, Optional

from .base import (
    AssistantMessage,
    AuthError,
    Provider,
    ProviderError,
    RateLimitError,
    ToolCall,
)

MAX_TOKENS = 8192

# The models worth offering, newest first. Claude is multimodal throughout, so
# the same model does the looking - there is no separate vision model to pick.
KNOWN_MODELS = {
    "claude-sonnet-5": "Balanced: the one to use (default)",
    "claude-opus-5": "Most capable, and the slowest",
    "claude-haiku-4-5-20251001": "Fast and cheap, still calls tools",
}

DEFAULT_MODEL = "claude-sonnet-5"


def _blocks(content) -> list:
    """Anthropic content as a list of blocks, whatever shape it arrived in."""
    if isinstance(content, list):
        return content
    if content:
        return [{"type": "text", "text": str(content)}]
    return []


def to_anthropic(messages: list) -> tuple:
    """(system, messages) in Claude's shape, from Vigil's OpenAI-shaped history.

    Two things need care. The system prompt is a parameter there rather than a
    message; and a run of tool results has to become one user message holding
    several blocks, because Claude rejects a tool_result that is not answering
    the assistant turn immediately before it.
    """
    system = ""
    converted: list = []
    pending_results: list = []

    def flush_results() -> None:
        if pending_results:
            converted.append({"role": "user", "content": list(pending_results)})
            pending_results.clear()

    for message in messages:
        role = message.get("role")

        if role == "system":
            text = message.get("content") or ""
            system = system + "\n\n" + text if system else text
            continue

        if role == "tool":
            pending_results.append({
                "type": "tool_result",
                "tool_use_id": message.get("tool_call_id") or "",
                "content": str(message.get("content") or ""),
            })
            continue

        flush_results()

        if role == "assistant":
            blocks = []
            if message.get("content"):
                blocks.append({"type": "text", "text": message["content"]})
            for call in message.get("tool_calls") or []:
                function = call.get("function") or {}
                raw = function.get("arguments") or "{}"
                try:
                    arguments = json.loads(raw) if raw.strip() else {}
                except json.JSONDecodeError:
                    arguments = {}
                blocks.append({
                    "type": "tool_use",
                    "id": call.get("id") or "",
                    "name": function.get("name") or "",
                    "input": arguments if isinstance(arguments, dict) else {},
                })
            if blocks:
                converted.append({"role": "assistant", "content": blocks})
            continue

        if role == "user":
            converted.append({"role": "user", "content": _blocks(message.get("content"))})

    flush_results()
    return system, converted


def to_anthropic_tools(tools: Optional[list]) -> list:
    """OpenAI tool definitions in the shape Claude wants."""
    converted = []
    for tool in tools or []:
        function = tool.get("function") or {}
        converted.append({
            "name": function.get("name") or "",
            "description": function.get("description") or "",
            "input_schema": function.get("parameters")
            or {"type": "object", "properties": {}},
        })
    return converted


class AnthropicProvider(Provider):
    name = "anthropic"

    def __init__(self, api_key: str, model: str = "", vision_model: str = "",
                 temperature: float = 0.3):
        if not api_key:
            raise AuthError(
                "No Anthropic key. Get one at "
                "https://console.anthropic.com/settings/keys"
            )
        try:
            from anthropic import Anthropic
        except ImportError as exc:  # pragma: no cover - install issue
            raise ProviderError(
                "The anthropic package is not installed. Run `pip install anthropic`."
            ) from exc

        self._client = Anthropic(api_key=api_key)
        self.model = model or DEFAULT_MODEL
        # Claude sees for itself, so there is no second model to configure
        self.vision_model = vision_model or self.model
        self.temperature = temperature
        self.total_tokens = 0
        self.request_count = 0

    # ------------------------------------------------------------------ chat
    def chat(
        self,
        messages: list,
        tools: Optional[list] = None,
        on_text: Optional[Callable[[str], None]] = None,
        model: Optional[str] = None,
        temperature: Optional[float] = None,
    ) -> AssistantMessage:
        system, converted = to_anthropic(messages)
        payload = {
            "model": model or self.model,
            "max_tokens": MAX_TOKENS,
            "temperature": self.temperature if temperature is None else temperature,
            "messages": converted,
        }
        if system:
            payload["system"] = system
        if tools:
            payload["tools"] = to_anthropic_tools(tools)

        self.request_count += 1
        try:
            if on_text is not None:
                return self._stream(payload, on_text)
            return self._once(payload)
        except Exception as exc:
            raise self._translate(exc) from exc

    def _once(self, payload: dict) -> AssistantMessage:
        response = self._client.messages.create(**payload)
        return self._read(response)

    def _stream(self, payload: dict, on_text: Callable[[str], None]) -> AssistantMessage:
        with self._client.messages.stream(**payload) as stream:
            for chunk in stream.text_stream:
                if chunk:
                    on_text(chunk)
            return self._read(stream.get_final_message())

    def _read(self, response) -> AssistantMessage:
        text_parts = []
        calls = []
        for block in response.content or []:
            kind = getattr(block, "type", "")
            if kind == "text":
                text_parts.append(getattr(block, "text", "") or "")
            elif kind == "tool_use":
                arguments = getattr(block, "input", None) or {}
                calls.append(ToolCall(
                    id=getattr(block, "id", "") or "",
                    name=getattr(block, "name", "") or "",
                    arguments=arguments if isinstance(arguments, dict) else {},
                    raw_arguments=json.dumps(arguments, ensure_ascii=False,
                                             default=str),
                ))

        usage = getattr(response, "usage", None)
        counted = {}
        if usage is not None:
            counted = {
                "prompt_tokens": getattr(usage, "input_tokens", 0) or 0,
                "completion_tokens": getattr(usage, "output_tokens", 0) or 0,
            }
            self.total_tokens += counted["prompt_tokens"] + counted["completion_tokens"]

        return AssistantMessage(
            content="".join(text_parts),
            tool_calls=calls,
            finish_reason=getattr(response, "stop_reason", "") or "",
            usage=counted,
        )

    # ---------------------------------------------------------------- vision
    def vision(self, prompt: str, image_b64: str, model: Optional[str] = None) -> str:
        try:
            response = self._client.messages.create(
                model=model or self.vision_model,
                max_tokens=1024,
                messages=[{
                    "role": "user",
                    "content": [
                        {"type": "image", "source": {
                            "type": "base64",
                            "media_type": "image/jpeg",
                            "data": image_b64,
                        }},
                        {"type": "text", "text": prompt},
                    ],
                }],
            )
        except Exception as exc:
            raise self._translate(exc) from exc

        return "".join(
            getattr(block, "text", "") or ""
            for block in (response.content or [])
            if getattr(block, "type", "") == "text"
        ).strip()

    # ----------------------------------------------------------------- meta
    def list_models(self) -> list:
        """What this key can reach, falling back to the ones we know about."""
        try:
            listed = self._client.models.list(limit=50)
            found = [item.id for item in getattr(listed, "data", []) or []]
            if found:
                return sorted(found)
        except Exception as exc:
            raise self._translate(exc) from exc
        return sorted(KNOWN_MODELS)

    @staticmethod
    def supports_vision(model_id: str) -> bool:
        return True          # every Claude model can see

    @staticmethod
    def supports_tools(model_id: str) -> bool:
        return True          # and every one of them can call tools

    @staticmethod
    def _translate(exc: Exception) -> ProviderError:
        text = str(exc)
        lowered = text.lower()
        if "authentication" in lowered or "invalid x-api-key" in lowered or "401" in text:
            return AuthError(
                "Anthropic did not accept that key. Check it at "
                "https://console.anthropic.com/settings/keys"
            )
        if "rate limit" in lowered or "429" in text:
            return RateLimitError("Anthropic rate limit reached. Wait a moment.")
        if "credit" in lowered or "billing" in lowered:
            return ProviderError(
                "That Anthropic key has no credit left. Top it up, or switch to "
                "the free provider in settings."
            )
        return ProviderError("Anthropic: " + text)
