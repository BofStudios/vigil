"""LLM providers.

To add a provider: implement the `Provider` class, then wire it into `build_provider`.
"""

from .anthropic_provider import AnthropicProvider
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
from .openai_provider import OpenAIProvider


def build_provider(config):
    """Create the provider selected by the `provider` field in the config."""
    chosen = getattr(config, "provider", "groq")
    if chosen == "anthropic":
        return AnthropicProvider(
            api_key=config.anthropic_api_key,
            model=config.anthropic_model,
            temperature=config.temperature,
        )
    if chosen == "openai":
        return OpenAIProvider(
            api_key=config.openai_api_key,
            model=config.openai_model,
            temperature=config.temperature,
            base_url=config.openai_base_url,
        )
    if chosen == "ollama":
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
    chosen = getattr(config, "provider", "groq")
    provider_class = {
        "ollama": OllamaProvider,
        "anthropic": AnthropicProvider,
        "openai": OpenAIProvider,
    }.get(chosen, GroqProvider)
    model = config.active_model
    if not provider_class.supports_tools(model):
        return (
            "'" + model + "' does not support tool calling - Vigil cannot work with it. "
            "Pick another model: vigil models"
        )
    return ""


__all__ = [
    "AnthropicProvider",
    "AssistantMessage",
    "AuthError",
    "GroqProvider",
    "KNOWN_MODELS",
    "OllamaProvider",
    "OpenAIProvider",
    "Provider",
    "ProviderError",
    "RateLimitError",
    "ToolCall",
    "build_provider",
    "provider_notes",
    "to_openai_tools",
    "tool_result_message",
]
