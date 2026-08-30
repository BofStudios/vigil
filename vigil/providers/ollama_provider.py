"""Ollama provider - fully local, needs no internet connection and no API key.

Install: https://ollama.com/download
Pull a model:
    ollama pull qwen3:8b          # supports tool calling
    ollama pull llama3.2-vision   # for screen analysis

Uses only the standard library - no extra dependency.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Callable, Optional

from .base import AssistantMessage, Provider, ProviderError, ToolCall

DEFAULT_HOST = "http://localhost:11434"
DEFAULT_MODEL = "qwen3:8b"
DEFAULT_VISION_MODEL = "llama3.2-vision"
REQUEST_TIMEOUT = 300

# Common model families that support tool calling (informational).
TOOL_CAPABLE_HINTS = ("qwen", "llama3.1", "llama3.2", "llama3.3", "mistral", "firefunction", "command-r", "hermes")
VISION_HINTS = ("vision", "llava", "-vl", "moondream", "bakllava", "gemma3")


class OllamaProvider(Provider):
    name = "ollama"

    def __init__(self, host: str = DEFAULT_HOST, model: str = DEFAULT_MODEL,
                 vision_model: str = DEFAULT_VISION_MODEL, temperature: float = 0.3):
        self.host = (host or DEFAULT_HOST).rstrip("/")
        self.model = model or DEFAULT_MODEL
        self.vision_model = vision_model or DEFAULT_VISION_MODEL
        self.temperature = temperature
        self.total_tokens = 0
        self.request_count = 0

    # ------------------------------------------------------------ requests
    def _post(self, path: str, payload: dict, stream: bool = False):
        request = urllib.request.Request(
            self.host + path,
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            response = urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT)
        except urllib.error.HTTPError as exc:
            body = ""
            try:
                body = exc.read().decode("utf-8", errors="replace")[:300]
            except Exception:
                pass
            raise self._translate(exc, body) from exc
        except urllib.error.URLError as exc:
            raise self._translate(exc, "") from exc

        if stream:
            return response
        try:
            return json.loads(response.read().decode("utf-8"))
        finally:
            response.close()

    def _get(self, path: str) -> dict:
        try:
            with urllib.request.urlopen(self.host + path, timeout=20) as response:
                return json.loads(response.read().decode("utf-8"))
        except (urllib.error.HTTPError, urllib.error.URLError, OSError) as exc:
            raise self._translate(exc, "") from exc

    # ---------------------------------------------------------------- chat
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
            "messages": _to_ollama_messages(messages),
            "stream": on_text is not None,
            "options": {"temperature": self.temperature if temperature is None else temperature},
        }
        if tools:
            payload["tools"] = tools

        self.request_count += 1
        if on_text is None:
            return self._to_message(self._post("/api/chat", payload))
        return self._chat_stream(payload, on_text)

    def _chat_stream(self, payload: dict, on_text: Callable[[str], None]) -> AssistantMessage:
        response = self._post("/api/chat", payload, stream=True)
        text_parts = []
        calls = []
        usage = {}
        try:
            for raw_line in response:
                line = raw_line.decode("utf-8", errors="replace").strip()
                if not line:
                    continue
                try:
                    chunk = json.loads(line)
                except json.JSONDecodeError:
                    continue
                message = chunk.get("message") or {}
                piece = message.get("content") or ""
                if piece:
                    text_parts.append(piece)
                    on_text(piece)
                for index, call in enumerate(message.get("tool_calls") or []):
                    calls.append(_to_tool_call(call, len(calls) + index))
                if chunk.get("done"):
                    usage = _usage_of(chunk)
        finally:
            response.close()

        self.total_tokens += usage.get("total_tokens", 0)
        return AssistantMessage(content="".join(text_parts), tool_calls=calls, finish_reason="stop", usage=usage)

    def _to_message(self, data: dict) -> AssistantMessage:
        message = data.get("message") or {}
        calls = [_to_tool_call(call, index) for index, call in enumerate(message.get("tool_calls") or [])]
        usage = _usage_of(data)
        self.total_tokens += usage.get("total_tokens", 0)
        return AssistantMessage(
            content=message.get("content") or "",
            tool_calls=calls,
            finish_reason=data.get("done_reason") or "stop",
            usage=usage,
        )

    # -------------------------------------------------------------- vision
    def vision(self, prompt: str, image_b64: str, model: Optional[str] = None) -> str:
        target = model or self.vision_model
        payload = {
            "model": target,
            "messages": [{"role": "user", "content": prompt, "images": [image_b64]}],
            "stream": False,
            "options": {"temperature": 0.2},
        }
        self.request_count += 1
        data = self._post("/api/chat", payload)
        return (data.get("message") or {}).get("content") or ""

    # -------------------------------------------------------------- models
    def list_models(self) -> list:
        data = self._get("/api/tags")
        return sorted(str(item.get("name")) for item in data.get("models", []) if item.get("name"))

    @staticmethod
    def supports_vision(model_id: str) -> bool:
        lowered = (model_id or "").lower()
        return any(hint in lowered for hint in VISION_HINTS)

    @staticmethod
    def supports_tools(model_id: str) -> bool:
        lowered = (model_id or "").lower()
        return any(hint in lowered for hint in TOOL_CAPABLE_HINTS)

    # -------------------------------------------------------------- errors
    def _translate(self, exc: Exception, body: str) -> ProviderError:
        text = (str(exc) + " " + body).lower()
        if "refused" in text or "urlopen error" in text:
            return ProviderError(
                "Could not reach Ollama at " + self.host + ". Is it installed and running?\n"
                "  1. Install from https://ollama.com/download\n"
                "  2. Run `ollama serve` (starts automatically on Windows)\n"
                "  3. Pull a model: `ollama pull " + DEFAULT_MODEL + "`"
            )
        if "not found" in text or "404" in text:
            return ProviderError(
                "Model not found: " + self.model + "\n"
                "  Pull it with: ollama pull " + self.model + "\n"
                "  Installed models: vigil models"
            )
        if "does not support tools" in text or ("tools" in text and "support" in text):
            return ProviderError(
                "This model does not support tool calling. Try a tool-capable one: "
                "ollama pull qwen3:8b"
            )
        return ProviderError("Ollama error: " + str(exc) + (" | " + body if body else ""))


# ---------------------------------------------------------------- helpers
def _to_ollama_messages(messages: list) -> list:
    """Convert OpenAI-shaped history into Ollama's format."""
    converted = []
    for message in messages:
        role = message.get("role")
        if role == "tool":
            converted.append(
                {
                    "role": "tool",
                    "content": message.get("content") or "",
                    "tool_name": message.get("name") or "",
                }
            )
            continue

        item = {"role": role, "content": message.get("content") or ""}
        if message.get("tool_calls"):
            item["tool_calls"] = [
                {
                    "function": {
                        "name": call["function"]["name"],
                        "arguments": _parse_arguments(call["function"].get("arguments")),
                    }
                }
                for call in message["tool_calls"]
            ]
        converted.append(item)
    return converted


def _parse_arguments(raw):
    if isinstance(raw, dict):
        return raw
    try:
        return json.loads(raw or "{}")
    except (json.JSONDecodeError, TypeError):
        return {}


def _to_tool_call(call: dict, index: int) -> ToolCall:
    function = call.get("function") or {}
    arguments = function.get("arguments")
    if isinstance(arguments, dict):
        raw = json.dumps(arguments, ensure_ascii=False)
        parsed = arguments
    else:
        raw = str(arguments or "{}")
        parsed = _parse_arguments(raw)
    return ToolCall(
        id=call.get("id") or ("ollama_call_" + str(index)),
        name=function.get("name") or "",
        arguments=parsed,
        raw_arguments=raw,
    )


def _usage_of(data: dict) -> dict:
    prompt_tokens = int(data.get("prompt_eval_count") or 0)
    completion_tokens = int(data.get("eval_count") or 0)
    return {
        "prompt_tokens": prompt_tokens,
        "completion_tokens": completion_tokens,
        "total_tokens": prompt_tokens + completion_tokens,
    }
