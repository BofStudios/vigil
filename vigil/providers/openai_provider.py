"""OpenAI, and anything that speaks its API.

The strategic point of this module: Vigil is not a model, it is the harness
around one. When a lab ships something better at driving a computer, the answer
is to run it here - behind the same approvals, the same blocked actions and the
same audit log - rather than to try to out-model a lab.

`base_url` is why this file covers more than OpenAI. Point it at OpenRouter, a
local vLLM, LM Studio, Together, or anything else that serves the same shape,
and it works unchanged.

Get a key: https://platform.openai.com/api-keys
"""

from __future__ import annotations

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

# Worth offering by name. The catalog moves; `vigil models` asks the endpoint
# itself, and anything not listed here still works if you type it in.
KNOWN_MODELS = {
    "gpt-6-astra": "Strongest at driving a computer (default)",
    "gpt-5.2": "Cheaper, still calls tools well",
    "gpt-5.2-mini": "Fast and cheap",
    "o4": "Reasoning-heavy, slower",
}

DEFAULT_MODEL = "gpt-6-astra"
DEFAULT_BASE_URL = ""      # empty means OpenAI's own endpoint

# Anything that cannot call tools cannot drive Vigil at all.
NO_TOOL_MODELS = {"gpt-3.5-turbo-instruct"}


class OpenAIProvider(Provider):
    name = "openai"

    def __init__(self, api_key: str, model: str = "", vision_model: str = "",
                 temperature: float = 0.3, base_url: str = ""):
        if not api_key:
            raise AuthError(
                "No OpenAI key. Get one at https://platform.openai.com/api-keys"
            )
        try:
            from openai import OpenAI
        except ImportError as exc:  # pragma: no cover - install issue
            raise ProviderError(
                "The openai package is not installed. Run `pip install openai`."
            ) from exc

        options = {"api_key": api_key}
        if base_url:
            options["base_url"] = base_url
        self._client = OpenAI(**options)

        self.base_url = base_url
        self.model = model or DEFAULT_MODEL
        # these models are multimodal, so the same one does the looking
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
        payload = {
            "model": model or self.model,
            "messages": messages,
            "temperature": self.temperature if temperature is None else temperature,
        }
        if tools:
            payload["tools"] = tools
            payload["tool_choice"] = "auto"

        self.request_count += 1
        try:
            if on_text is not None:
                return self._stream(payload, on_text)
            return self._once(payload)
        except Exception as exc:
            raise self._translate(exc) from exc

    def _once(self, payload: dict) -> AssistantMessage:
        response = self._client.chat.completions.create(**payload)
        choice = response.choices[0]
        self._count(getattr(response, "usage", None))

        return AssistantMessage(
            content=choice.message.content or "",
            tool_calls=[
                ToolCall.from_raw(call.id, call.function.name, call.function.arguments)
                for call in (choice.message.tool_calls or [])
            ],
            finish_reason=choice.finish_reason or "",
            usage=self._usage(getattr(response, "usage", None)),
        )

    def _stream(self, payload: dict, on_text: Callable[[str], None]) -> AssistantMessage:
        """Text arrives in pieces; tool calls arrive in pieces too, and have to
        be reassembled by index before they mean anything."""
        text_parts: list = []
        building: dict = {}
        finish = ""
        usage = None

        stream = self._client.chat.completions.create(
            **payload, stream=True, stream_options={"include_usage": True}
        )
        for chunk in stream:
            if getattr(chunk, "usage", None):
                usage = chunk.usage
            if not chunk.choices:
                continue
            delta = chunk.choices[0].delta
            if chunk.choices[0].finish_reason:
                finish = chunk.choices[0].finish_reason

            if getattr(delta, "content", None):
                text_parts.append(delta.content)
                on_text(delta.content)

            for piece in getattr(delta, "tool_calls", None) or []:
                slot = building.setdefault(
                    piece.index, {"id": "", "name": "", "arguments": ""}
                )
                if piece.id:
                    slot["id"] = piece.id
                if piece.function and piece.function.name:
                    slot["name"] = piece.function.name
                if piece.function and piece.function.arguments:
                    slot["arguments"] += piece.function.arguments

        self._count(usage)
        return AssistantMessage(
            content="".join(text_parts),
            tool_calls=[
                ToolCall.from_raw(slot["id"], slot["name"], slot["arguments"])
                for _index, slot in sorted(building.items())
                if slot["name"]
            ],
            finish_reason=finish,
            usage=self._usage(usage),
        )

    # ---------------------------------------------------------------- vision
    def vision(self, prompt: str, image_b64: str, model: Optional[str] = None) -> str:
        try:
            response = self._client.chat.completions.create(
                model=model or self.vision_model,
                messages=[{
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt},
                        {"type": "image_url", "image_url": {
                            "url": "data:image/jpeg;base64," + image_b64,
                        }},
                    ],
                }],
                max_tokens=1024,
            )
        except Exception as exc:
            raise self._translate(exc) from exc
        return (response.choices[0].message.content or "").strip()

    # ------------------------------------------------------------ transcribe
    def transcribe(self, audio: bytes, filename: str = "speech.wav",
                   language: str = "en") -> str:
        try:
            response = self._client.audio.transcriptions.create(
                file=(filename, audio),
                model="whisper-1",
                language=language,
            )
        except Exception as exc:
            raise self._translate(exc) from exc
        return (getattr(response, "text", "") or "").strip()

    # ------------------------------------------------------------------ meta
    def list_models(self) -> list:
        try:
            listed = self._client.models.list()
            found = [item.id for item in listed.data]
            return sorted(found) if found else sorted(KNOWN_MODELS)
        except Exception as exc:
            raise self._translate(exc) from exc

    def _count(self, usage) -> None:
        if usage is not None:
            self.total_tokens += getattr(usage, "total_tokens", 0) or 0

    @staticmethod
    def _usage(usage) -> dict:
        if usage is None:
            return {}
        return {
            "prompt_tokens": getattr(usage, "prompt_tokens", 0) or 0,
            "completion_tokens": getattr(usage, "completion_tokens", 0) or 0,
        }

    @staticmethod
    def supports_vision(model_id: str) -> bool:
        return True

    @staticmethod
    def supports_tools(model_id: str) -> bool:
        return model_id not in NO_TOOL_MODELS

    @staticmethod
    def _translate(exc: Exception) -> ProviderError:
        text = str(exc)
        lowered = text.lower()
        if "api key" in lowered or "unauthorized" in lowered or "401" in text:
            return AuthError(
                "That key was not accepted. Check it at "
                "https://platform.openai.com/api-keys"
            )
        if "rate limit" in lowered or "429" in text:
            return RateLimitError("Rate limit reached. Wait a moment and try again.")
        if "quota" in lowered or "billing" in lowered or "credit" in lowered:
            return ProviderError(
                "That key has no credit left. Top it up, or switch to the free "
                "provider in settings."
            )
        if "does not exist" in lowered or "model_not_found" in lowered:
            return ProviderError(
                "That model is not available to this key yet. Newly released "
                "models are often limited at first - `vigil models` lists what "
                "you can actually reach."
            )
        return ProviderError("OpenAI: " + text)
