"""The three ways to give Vigil something to think with.

Same idea as brains.py: the words shown on the first screen and the thing they
actually do live in one place, so the promise on the screen cannot drift away
from what happens when you press the button.

Free comes first, and is the default, because it is the honest answer to "do I
have to pay for this". Bringing your own Claude key is second, for people who
already have one. Offline is last, because it asks the most of the machine.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .config import DEFAULT_ANTHROPIC_MODEL, DEFAULT_MODEL, DEFAULT_OLLAMA_MODEL
from .providers.anthropic_provider import KNOWN_MODELS as CLAUDE_MODELS
from .providers.openai_provider import KNOWN_MODELS as OPENAI_MODELS


@dataclass(frozen=True)
class Route:
    """One way in, and everything the first screen needs to draw it."""

    key: str                 # the provider id
    name: str                # what the tab says
    badge: str               # the small word beside it
    blurb: str               # one or two sentences under the tabs
    models: list = field(default_factory=list)   # (id, what it is for)
    link: str = ""           # where the key comes from
    link_text: str = ""
    placeholder: str = ""
    needs_key: bool = True

    def describe(self) -> dict:
        return {
            "key": self.key,
            "name": self.name,
            "badge": self.badge,
            "blurb": self.blurb,
            "models": [{"id": model, "note": note} for model, note in self.models],
            "link": self.link,
            "link_text": self.link_text,
            "placeholder": self.placeholder,
            "needs_key": self.needs_key,
        }


FREE = Route(
    key="groq",
    name="Free",
    badge="no card",
    blurb=(
        "Groq's free tier, and it stays free. Two models come with it - Vigil "
        "moves between them when you switch how it thinks."
    ),
    models=[
        (DEFAULT_MODEL.replace("120b", "20b"), "fast, for everyday jobs"),
        (DEFAULT_MODEL, "stronger, for problems"),
    ],
    link="https://console.groq.com/keys",
    link_text="Get a free key",
    placeholder="gsk_…",
)

CLAUDE = Route(
    key="anthropic",
    name="Claude",
    badge="your key",
    blurb=(
        "Bring your own Anthropic key and Vigil runs on Claude. Not free, but "
        "it is the strongest thing you can point at your own machine."
    ),
    models=[(model, note) for model, note in list(CLAUDE_MODELS.items())[:3]],
    link="https://console.anthropic.com/settings/keys",
    link_text="Get a Claude key",
    placeholder="sk-ant-…",
)

GPT = Route(
    key="openai",
    name="GPT",
    badge="your key",
    blurb=(
        "Your own OpenAI key. Vigil is the harness, not the model - when a lab "
        "ships something better at driving a computer, you run it in here, "
        "behind the same approvals and the same blocked actions."
    ),
    models=[(model, note) for model, note in list(OPENAI_MODELS.items())[:3]],
    link="https://platform.openai.com/api-keys",
    link_text="Get an OpenAI key",
    placeholder="sk-…",
)

OFFLINE = Route(
    key="ollama",
    name="Offline",
    badge="local",
    blurb=(
        "Ollama runs the model on this computer. Nothing is sent anywhere at "
        "all - no key, no account, no network."
    ),
    models=[(DEFAULT_OLLAMA_MODEL, "pull it with: ollama pull " + DEFAULT_OLLAMA_MODEL)],
    link="https://ollama.com/download",
    link_text="Install Ollama",
    placeholder="http://localhost:11434",
    needs_key=False,
)

ALL = (FREE, CLAUDE, GPT, OFFLINE)
DEFAULT = FREE.key


def routes() -> list:
    """Everything the first screen needs, in the order it should offer them."""
    return [route.describe() for route in ALL]


def get(key: str) -> Route:
    """The named route, or the free one when the name means nothing."""
    wanted = (key or "").strip().lower()
    for route in ALL:
        if route.key == wanted:
            return route
    return FREE


__all__ = ["ALL", "CLAUDE", "DEFAULT", "DEFAULT_ANTHROPIC_MODEL", "FREE", "GPT",
           "OFFLINE", "Route", "get", "routes"]
