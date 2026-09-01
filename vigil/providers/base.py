"""LLM provider interface. To add a provider, implement the Provider class."""

from __future__ import annotations

import json
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, Callable, Optional


class ProviderError(RuntimeError):
    """Provider-side failure (network, authentication, quota, ...)."""


class AuthError(ProviderError):
    """API key missing or invalid."""


class RateLimitError(ProviderError):
    """Quota or rate limit exceeded."""


@dataclass
class ToolCall:
    id: str
    name: str
    arguments: dict = field(default_factory=dict)
    raw_arguments: str = ""

    @classmethod
    def from_raw(cls, call_id: str, name: str, raw: str) -> ToolCall:
        try:
            parsed = json.loads(raw) if raw and raw.strip() else {}
            if not isinstance(parsed, dict):
                parsed = {"value": parsed}
        except json.JSONDecodeError:
            parsed = {}
        return cls(id=call_id, name=name, arguments=parsed, raw_arguments=raw or "")


@dataclass
class AssistantMessage:
    content: str = ""
    tool_calls: list = field(default_factory=list)
    finish_reason: str = ""
    usage: dict = field(default_factory=dict)

    @property
    def wants_tools(self) -> bool:
        return bool(self.tool_calls)

    def to_message(self) -> dict:
        """OpenAI-compatible message dict to append to the conversation."""
        message: dict = {"role": "assistant", "content": self.content or ""}
        if self.tool_calls:
            message["tool_calls"] = [
                {
                    "id": call.id,
                    "type": "function",
                    "function": {"name": call.name, "arguments": call.raw_arguments or "{}"},
                }
                for call in self.tool_calls
            ]
        return message


class Provider(ABC):
    """Interface every LLM provider must implement."""

    name = "provider"

    @abstractmethod
    def chat(
        self,
        messages: list,
        tools: Optional[list] = None,
        on_text: Optional[Callable[[str], None]] = None,
        model: Optional[str] = None,
        temperature: Optional[float] = None,
    ) -> AssistantMessage:
        """Chat completion. When on_text is given, text is streamed chunk by chunk."""

    @abstractmethod
    def vision(self, prompt: str, image_b64: str, model: Optional[str] = None) -> str:
        """Analyse an image and return a text description."""

    def transcribe(self, audio: bytes, filename: str = "speech.wav", language: str = "en") -> str:
        """Turn spoken audio into text. Providers that cannot should raise."""
        raise ProviderError("This provider cannot transcribe audio.")

    def list_models(self) -> list:
        """Available model ids. Empty list when unsupported."""
        return []

    def check(self) -> tuple:
        """Health check returning (ok, message)."""
        try:
            models = self.list_models()
            return True, str(len(models)) + " models reachable"
        except ProviderError as exc:
            return False, str(exc)


def to_openai_tools(specs: list) -> list:
    """Convert ToolSpec objects into OpenAI/Groq compatible tool definitions."""
    return [
        {
            "type": "function",
            "function": {
                "name": spec.name,
                "description": spec.description,
                "parameters": spec.parameters,
            },
        }
        for spec in specs
    ]


def tool_result_message(call_id: str, name: str, content: Any) -> dict:
    """Turn a tool result into a message for the conversation history."""
    if not isinstance(content, str):
        content = json.dumps(content, ensure_ascii=False, default=str)
    return {"role": "tool", "tool_call_id": call_id, "name": name, "content": content}
