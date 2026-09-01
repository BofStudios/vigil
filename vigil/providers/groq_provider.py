"""Groq provider - the default brain, running on a free API key.

Get a key: https://console.groq.com/keys
"""

from __future__ import annotations

import time
from typing import Callable, Optional

from .base import (
    AssistantMessage,
    AuthError,
    Provider,
    ProviderError,
    RateLimitError,
    ToolCall,
)

MAX_RETRIES = 3
RETRY_BASE_DELAY = 2.0

# Commonly used Groq models. The catalog changes over time - run `vigil models` for the live list.
KNOWN_MODELS = {
    "openai/gpt-oss-120b": "Strong reasoning + tool use (default)",
    "openai/gpt-oss-20b": "Faster and lighter, still supports tools",
    "qwen/qwen3.8-27b": "Multilingual, tool use + VISION support",
    "qwen/qwen3.6-27b": "Multilingual, tool use",
    "groq/compound": "Groq agent system - does NOT support tool calling",
    "groq/compound-mini": "Groq agent system - does NOT support tool calling",
    "allam-2-7b": "Small Arabic-focused model",
}

# Models that accept images. Not every tool-capable model can see, which is why
# screen analysis is routed to a separate vision model.
VISION_MODELS = {"qwen/qwen3.8-27b"}
VISION_HINTS = ("qwen3.8", "scout", "maverick", "vision", "llava", "-vl")

# Models that cannot call tools - the user is warned when one is selected.
NO_TOOL_MODELS = {"groq/compound", "groq/compound-mini", "allam-2-7b"}

# Speech to text. Turbo is the one to use: same accuracy, a fraction of the wait.
SPEECH_MODEL = "whisper-large-v3-turbo"


class GroqProvider(Provider):
    name = "groq"

    def __init__(self, api_key: str, model: str, vision_model: str, temperature: float = 0.3):
        if not api_key:
            raise AuthError(
                "GROQ_API_KEY not found. Get a free key at https://console.groq.com/keys "
                "then run `vigil setup`."
            )
        try:
            from groq import Groq
        except ImportError as exc:  # pragma: no cover - install issue
            raise ProviderError("The groq package is not installed. Run `pip install groq`.") from exc

        self._client = Groq(api_key=api_key)
        self.model = model
        self.vision_model = vision_model
        self.temperature = temperature
        self.total_tokens = 0
        self.request_count = 0

    # ------------------------------------------------------------------
    # chat
    # ------------------------------------------------------------------
    def chat(
        self,
        messages: list,
        tools: Optional[list] = None,
        on_text: Optional[Callable[[str], None]] = None,
        model: Optional[str] = None,
        temperature: Optional[float] = None,
    ) -> AssistantMessage:
        kwargs = {
            "model": model or self.model,
            "messages": messages,
            "temperature": self.temperature if temperature is None else temperature,
        }
        if tools:
            kwargs["tools"] = tools
            kwargs["tool_choice"] = "auto"

        stream = on_text is not None
        last_error: Optional[Exception] = None

        for attempt in range(MAX_RETRIES):
            try:
                if stream:
                    return self._chat_stream(kwargs, on_text)
                return self._chat_once(kwargs)
            except RateLimitError as exc:
                last_error = exc
                if attempt == MAX_RETRIES - 1:
                    raise
                time.sleep(RETRY_BASE_DELAY * (2**attempt))
            except ProviderError:
                raise
            except Exception as exc:  # network errors
                last_error = exc
                if attempt == MAX_RETRIES - 1:
                    raise self._translate(exc) from exc
                time.sleep(RETRY_BASE_DELAY * (2**attempt))

        raise ProviderError(str(last_error))

    def _chat_once(self, kwargs: dict) -> AssistantMessage:
        try:
            response = self._client.chat.completions.create(**kwargs)
        except Exception as exc:
            raise self._translate(exc) from exc

        self.request_count += 1
        choice = response.choices[0]
        message = choice.message
        calls = [
            ToolCall.from_raw(call.id, call.function.name, call.function.arguments)
            for call in (getattr(message, "tool_calls", None) or [])
        ]

        usage = {}
        if getattr(response, "usage", None):
            usage = {
                "prompt_tokens": getattr(response.usage, "prompt_tokens", 0),
                "completion_tokens": getattr(response.usage, "completion_tokens", 0),
                "total_tokens": getattr(response.usage, "total_tokens", 0),
            }
            self.total_tokens += usage.get("total_tokens", 0)

        return AssistantMessage(
            content=message.content or "",
            tool_calls=calls,
            finish_reason=choice.finish_reason or "",
            usage=usage,
        )

    def _chat_stream(self, kwargs: dict, on_text: Callable[[str], None]) -> AssistantMessage:
        try:
            stream = self._client.chat.completions.create(stream=True, **kwargs)
        except Exception as exc:
            raise self._translate(exc) from exc

        self.request_count += 1
        text_parts = []
        partial_calls: dict = {}
        finish_reason = ""
        usage = {}

        for chunk in stream:
            if getattr(chunk, "usage", None):
                usage = {
                    "prompt_tokens": getattr(chunk.usage, "prompt_tokens", 0),
                    "completion_tokens": getattr(chunk.usage, "completion_tokens", 0),
                    "total_tokens": getattr(chunk.usage, "total_tokens", 0),
                }
            if not chunk.choices:
                continue
            choice = chunk.choices[0]
            if choice.finish_reason:
                finish_reason = choice.finish_reason
            delta = choice.delta
            if delta is None:
                continue

            if getattr(delta, "content", None):
                text_parts.append(delta.content)
                on_text(delta.content)

            for call_delta in getattr(delta, "tool_calls", None) or []:
                index = getattr(call_delta, "index", 0) or 0
                slot = partial_calls.setdefault(index, {"id": "", "name": "", "args": ""})
                if getattr(call_delta, "id", None):
                    slot["id"] = call_delta.id
                function = getattr(call_delta, "function", None)
                if function is not None:
                    if getattr(function, "name", None):
                        slot["name"] = function.name
                    if getattr(function, "arguments", None):
                        slot["args"] += function.arguments

        self.total_tokens += usage.get("total_tokens", 0)
        calls = [
            ToolCall.from_raw(slot["id"] or ("call_" + str(index)), slot["name"], slot["args"])
            for index, slot in sorted(partial_calls.items())
            if slot["name"]
        ]
        return AssistantMessage(
            content="".join(text_parts),
            tool_calls=calls,
            finish_reason=finish_reason,
            usage=usage,
        )

    # ------------------------------------------------------------------
    # vision
    # ------------------------------------------------------------------
    def vision(self, prompt: str, image_b64: str, model: Optional[str] = None) -> str:
        target = model or self.vision_model
        if not self.supports_vision(target):
            raise ProviderError(
                "Model '" + target + "' does not support images. Pick a vision model:\n"
                "  vigil config set vision_model qwen/qwen3.8-27b\n"
                "(run `vigil models` for the live list)"
            )
        messages = [
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": prompt},
                    {
                        "type": "image_url",
                        "image_url": {"url": "data:image/jpeg;base64," + image_b64},
                    },
                ],
            }
        ]
        try:
            response = self._client.chat.completions.create(
                model=target, messages=messages, temperature=0.2, max_tokens=1400
            )
        except Exception as exc:
            raise self._translate(exc) from exc
        self.request_count += 1
        return response.choices[0].message.content or ""

    # ------------------------------------------------------------------
    # speech
    # ------------------------------------------------------------------
    def transcribe(self, audio: bytes, filename: str = "speech.wav", language: str = "en") -> str:
        """Send recorded audio to Whisper and get the words back."""
        if not audio:
            return ""
        try:
            response = self._client.audio.transcriptions.create(
                file=(filename, audio),
                model=SPEECH_MODEL,
                language=language or None,
                response_format="text",
                temperature=0,
            )
        except Exception as exc:
            raise self._translate(exc) from exc
        self.request_count += 1
        # response_format="text" gives a bare string, but the SDK has returned an
        # object with .text in the past; accept either.
        return (response if isinstance(response, str) else getattr(response, "text", "")).strip()

    # ------------------------------------------------------------------
    # models
    # ------------------------------------------------------------------
    def list_models(self) -> list:
        try:
            response = self._client.models.list()
        except Exception as exc:
            raise self._translate(exc) from exc
        ids = []
        for item in getattr(response, "data", []) or []:
            model_id = getattr(item, "id", None)
            if model_id and not str(model_id).startswith(("whisper", "distil-whisper", "playai-tts")):
                ids.append(str(model_id))
        return sorted(ids)

    @staticmethod
    def supports_vision(model_id: str) -> bool:
        lowered = (model_id or "").lower()
        if lowered in VISION_MODELS:
            return True
        return any(hint in lowered for hint in VISION_HINTS)

    @staticmethod
    def supports_tools(model_id: str) -> bool:
        return (model_id or "").lower() not in NO_TOOL_MODELS

    # ------------------------------------------------------------------
    @staticmethod
    def _translate(exc: Exception) -> ProviderError:
        text = str(exc)
        lowered = text.lower()
        if "401" in text or "invalid api key" in lowered or "unauthorized" in lowered:
            return AuthError(
                "Invalid API key. Get a new one at https://console.groq.com/keys "
                "and run `vigil setup`."
            )
        if "429" in text or "rate limit" in lowered or "quota" in lowered:
            return RateLimitError("Groq rate/quota limit reached. Wait a moment and try again.")
        if "404" in text and "model" in lowered:
            return ProviderError(
                "Model not found or retired. The Groq catalog changes over time - "
                "run `vigil models` for the live list, then `vigil config set model <name>`."
            )
        if "content must be a string" in lowered:
            return ProviderError(
                "The selected model does not accept images. Pick a vision model: "
                "vigil config set vision_model qwen/qwen3.8-27b"
            )
        if "tool calling" in lowered and "not supported" in lowered:
            return ProviderError(
                "The selected model does not support tool calling (Vigil cannot work without it). "
                "Suggested: vigil config set model openai/gpt-oss-120b"
            )
        if "connection" in lowered or "timeout" in lowered or "network" in lowered:
            return ProviderError("Connection error. Check your internet connection.")
        return ProviderError(text)
