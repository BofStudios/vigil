"""First run, without a terminal.

Vigil is an application now: someone who buys it double-clicks an icon and never
sees a shell. That makes a missing key a screen rather than an error printed
somewhere nobody is looking, and these tests are about that path.
"""

import pytest

from vigil.config import Config
from vigil.desktop import app as app_module
from vigil.desktop.app import Api


@pytest.fixture
def blank():
    """A config with nothing configured - what a fresh install looks like."""
    settings = Config()
    settings.api_key = ""
    settings.provider = "groq"
    return settings


@pytest.fixture
def ready():
    settings = Config()
    settings.api_key = "a-key-that-works"
    return settings


class _Provider:
    """A provider that answers, or does not."""

    name = "fake"

    def __init__(self, working=True, note="12 models reachable"):
        self.working = working
        self.note = note
        self.model = "openai/gpt-oss-20b"
        self.total_tokens = 0
        self.request_count = 0

    def check(self):
        return self.working, self.note

    def chat(self, *args, **kwargs):
        return None

    def vision(self, *args, **kwargs):
        return ""

    def list_models(self):
        return []


# ------------------------------------------------------------- what it needs
def test_a_fresh_install_asks_for_a_key(blank):
    api = Api(blank)
    setup = api.state()["setup"]

    assert setup["needed"] is True
    assert setup["provider"] == "groq"
    assert setup["reason"] == ""       # nothing has gone wrong yet, it is just new


def test_once_it_can_think_the_screen_goes_away(ready):
    api = Api(ready)
    api.new_tab()
    assert api.state()["setup"]["needed"] is False


def test_an_unreachable_ollama_says_where_it_looked(blank):
    blank.provider = "ollama"
    api = Api(blank)
    setup = api.state()["setup"]

    assert setup["needed"] is True
    assert blank.ollama_host in setup["reason"]


def test_a_key_that_was_refused_is_explained_rather_than_hidden(ready):
    api = Api(ready)
    api._setup_error = "Groq says that key is not valid."
    assert "not valid" in api.state()["setup"]["reason"]


# ------------------------------------------------------------- connecting
def test_a_working_key_is_saved_and_the_app_fills_in(blank, monkeypatch):
    monkeypatch.setattr(app_module, "build_provider", lambda config: _Provider())
    api = Api(blank)

    result = api.connect("groq", "gsk_something")

    assert result["ok"] is True
    assert result["state"]["setup"]["needed"] is False
    assert api.config.api_key == "gsk_something"
    assert api.sessions, "a session should exist the moment it can think"


def test_nothing_is_saved_until_the_key_actually_answers(blank, monkeypatch):
    monkeypatch.setattr(
        app_module, "build_provider",
        lambda config: _Provider(working=False, note="Groq says that key is not valid."),
    )
    api = Api(blank)

    result = api.connect("groq", "gsk_wrong")

    assert "not valid" in result["error"]
    assert api.config.api_key == ""        # untouched
    assert api.sessions == {}


def test_a_provider_that_raises_is_reported_as_words(blank, monkeypatch):
    def explode(config):
        raise RuntimeError("could not reach groq.com")

    monkeypatch.setattr(app_module, "build_provider", explode)
    api = Api(blank)

    assert "could not reach" in api.connect("groq", "gsk_x")["error"]


def test_an_empty_key_is_caught_before_anything_is_tried(blank):
    api = Api(blank)
    assert api.connect("groq", "   ")["error"] == "Paste your key first."


def test_choosing_ollama_needs_no_key(blank, monkeypatch):
    monkeypatch.setattr(app_module, "build_provider", lambda config: _Provider())
    api = Api(blank)

    result = api.connect("ollama", "", "http://localhost:11434")

    assert result["ok"] is True
    assert api.config.provider == "ollama"


def test_a_provider_nobody_has_heard_of_is_refused(blank):
    api = Api(blank)
    assert "Unknown provider" in api.connect("skynet", "x")["error"]


def test_a_claude_key_goes_to_the_claude_field(blank, monkeypatch):
    """Not into the Groq one, which would then be quietly wrong."""
    monkeypatch.setattr(app_module, "build_provider", lambda config: _Provider())
    api = Api(blank)

    result = api.connect("anthropic", "sk-ant-something")

    assert result["ok"] is True
    assert api.config.anthropic_api_key == "sk-ant-something"
    assert api.config.api_key == ""
    assert api.config.provider == "anthropic"


def test_claude_with_no_key_is_caught_too(blank):
    api = Api(blank)
    assert api.connect("anthropic", "")["error"] == "Paste your key first."


def test_an_openai_key_goes_to_the_openai_field(blank, monkeypatch):
    """Vigil is the harness, so the newest model from anywhere can run in it."""
    monkeypatch.setattr(app_module, "build_provider", lambda config: _Provider())
    api = Api(blank)

    result = api.connect("openai", "sk-proj-something")

    assert result["ok"] is True
    assert api.config.openai_api_key == "sk-proj-something"
    assert api.config.api_key == ""
    assert api.config.anthropic_api_key == ""
    assert api.config.provider == "openai"


def test_the_first_screen_offers_every_way_in(blank):
    offered = Api(blank).state()["setup"]["routes"]
    assert [route["key"] for route in offered] == [
        "groq", "anthropic", "openai", "ollama"]
    assert offered[0]["models"], "the free route should name what it gives you"
    assert offered[-1]["needs_key"] is False


def test_changing_the_key_later_reaches_the_session_already_open(ready, monkeypatch):
    """Otherwise the running tab keeps using the key you just replaced."""
    monkeypatch.setattr(app_module, "build_provider", lambda config: _Provider())
    api = Api(ready)
    api.new_tab()
    before = next(iter(api.sessions.values())).agent.provider

    api.connect("groq", "a-different-key")

    after = next(iter(api.sessions.values())).agent.provider
    assert after is not before
    assert api.config.api_key == "a-different-key"


def test_the_error_is_cleared_once_it_works(blank, monkeypatch):
    monkeypatch.setattr(app_module, "build_provider", lambda config: _Provider())
    api = Api(blank)
    api._setup_error = "an old failure"

    api.connect("groq", "gsk_good")
    assert api._setup_error == ""


# ------------------------------------------------------------- opening links
def test_only_the_links_vigil_knows_about_are_opened(blank, monkeypatch):
    opened = []
    import webbrowser

    monkeypatch.setattr(webbrowser, "open", lambda url: opened.append(url))
    api = Api(blank)

    assert api.open_url("https://console.groq.com/keys")["ok"] is True
    assert opened == ["https://console.groq.com/keys"]


def test_anything_else_is_refused(blank, monkeypatch):
    opened = []
    import webbrowser

    monkeypatch.setattr(webbrowser, "open", lambda url: opened.append(url))
    api = Api(blank)

    for url in ("https://evil.example/steal", "file:///C:/Windows",
                "https://console.groq.com/keys?next=evil"):
        assert "error" in api.open_url(url)
    assert opened == []


# --------------------------------------------------------- no terminal needed
def _function(source: str, name: str) -> str:
    """One top-level function's body, up to the next one."""
    start = source.index("def " + name + "(")
    rest = source[start:]
    following = rest.find("\ndef ", 1)
    return rest if following == -1 else rest[:following]


def test_opening_the_app_no_longer_demands_a_key_first():
    """The gate used to print to a terminal a double-clicked app does not have."""
    from pathlib import Path

    cli = (Path(__file__).resolve().parent.parent / "vigil" / "cli.py").read_text(
        encoding="utf-8"
    )
    assert "_require_key" not in _function(cli, "cmd_app")


def test_a_terminal_session_still_wants_one_up_front():
    """There the message can be printed, and read, so the check earns its place."""
    from pathlib import Path

    cli = (Path(__file__).resolve().parent.parent / "vigil" / "cli.py").read_text(
        encoding="utf-8"
    )
    assert "_require_key" in _function(cli, "cmd_chat")


# -------------------------------------------------------------- the routes
def test_free_is_offered_first_and_is_the_default():
    """It is the honest answer to "do I have to pay for this"."""
    from vigil import routes

    assert routes.ALL[0].key == "groq"
    assert routes.DEFAULT == "groq"


def test_every_route_can_be_drawn():
    from vigil.routes import routes

    for route in routes():
        for field in ("key", "name", "badge", "blurb", "placeholder"):
            assert route[field], (route["key"], field)
        assert isinstance(route["models"], list)


def test_only_the_offline_route_needs_no_key():
    from vigil.routes import routes

    needs = {route["key"]: route["needs_key"] for route in routes()}
    assert needs == {"groq": True, "anthropic": True,
                     "openai": True, "ollama": False}


def test_the_free_route_names_the_two_models_the_brains_use():
    """The picker promise and the brain models have to be the same two."""
    from vigil import brains
    from vigil.routes import FREE

    offered = [model for model, _note in FREE.models]
    assert offered == [brains.get("direct").model, brains.get("autonomous").model]


def test_an_unknown_route_falls_back_to_the_free_one():
    from vigil.routes import get

    assert get("wormhole").key == "groq"
    assert get("").key == "groq"
    assert get(None).key == "groq"


def test_the_route_keys_are_real_providers():
    from vigil.config import PROVIDERS
    from vigil.routes import routes

    for route in routes():
        assert route["key"] in PROVIDERS, route["key"]
