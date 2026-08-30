"""LLM providers.

To add a provider: implement the `Provider` class, then wire it into `build_provider`.
"""

from .base import (
    AssistantMessage,
    AuthError,
    Provider,
    ProviderError,
    RateLimitError,
    ToolCall,
    to_openai_tools,
    tool_result_message,
)
from .groq_provider import KNOWN_MODELS, GroqProvider
from .ollama_provider import OllamaProvider


def build_provider(config):
    """Create the provider selected by the `provider` field in the config."""
    if getattr(config, "provider", "groq") == "ollama":
        return OllamaProvider(
            host=config.ollama_host,
            model=config.ollama_model,
            vision_model=config.ollama_vision_model,
            temperature=config.temperature,
        )
    return GroqProvider(
        api_key=config.api_key,
        model=config.model,
        vision_model=config.vision_model,
        temperature=config.temperature,
    )


def provider_notes(config) -> str:
    """Warning text for the selected model (no tool or vision support)."""
    provider_class = OllamaProvider if getattr(config, "provider", "groq") == "ollama" else GroqProvider
    model = config.active_model
    if not provider_class.supports_tools(model):
        return (
            "'" + model + "' does not support tool calling - Vigil cannot work with it. "
            "Pick another model: vigil models"
        )
    return ""


__all__ = [
    "AssistantMessage",
    "AuthError",
    "GroqProvider",
    "KNOWN_MODELS",
    "OllamaProvider",
    "Provider",
    "ProviderError",
    "RateLimitError",
    "ToolCall",
    "build_provider",
    "provider_notes",
    "to_openai_tools",
    "tool_result_message",
]
