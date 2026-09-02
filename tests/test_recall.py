"""What the bar remembers, and what it will take from the clipboard.

The clipboard rule is the one worth guarding: Vigil reads the list of files
Explorer puts there and nothing else. Copied text is usually something personal
and should never be handed to an agent because a key was pressed.
"""

import base64
import io

import pytest

from vigil.config import Config
from vigil.desktop import recall
from vigil.desktop.app import Api
from vigil.desktop.recall import History, quote


@pytest.fixture
def config():
    settings = Config()
    settings.api_key = "not-a-real-key"
    return settings


@pytest.fixture
def history(tmp_path):
    return History(path=tmp_path / "bar-history.json")


# ------------------------------------------------------------------- history
def test_it_starts_with_nothing(history):
    assert history.items == []


def test_what_you_asked_comes_back(history):
    history.add("open my downloads folder")
    assert history.items == ["open my downloads folder"]


def test_blank_prompts_are_not_worth_keeping(history):
    history.add("   ")
    history.add("")
    assert history.items == []


def test_asking_the_same_thing_twice_running_stores_it_once(history):
    history.add("open downloads")
    history.add("open downloads")
    assert history.items == ["open downloads"]


def test_repeating_an_older_prompt_moves_it_to_the_end(history):
    """Otherwise the list fills with the same few things at different depths."""
    for prompt in ("first", "second", "third", "first"):
        history.add(prompt)
    assert history.items == ["second", "third", "first"]


def test_it_survives_the_app_closing(history):
    history.add("sort my screenshots")
    assert History(path=history.path).items == ["sort my screenshots"]


def test_only_so_much_is_kept(tmp_path):
    small = History(path=tmp_path / "h.json", keep=3)
    for index in range(10):
        small.add("prompt " + str(index))
    assert small.items == ["prompt 7", "prompt 8", "prompt 9"]


def test_a_corrupt_history_file_is_not_fatal(tmp_path):
    broken = tmp_path / "h.json"
    broken.write_text("{ this is not json", encoding="utf-8")
    assert History(path=broken).items == []


def test_a_history_file_holding_the_wrong_shape_is_ignored(tmp_path):
    odd = tmp_path / "h.json"
    odd.write_text('["fine", null, 12, "  ", {"x": 1}]', encoding="utf-8")
    assert History(path=odd).items == ["fine"]


def test_clearing_it_empties_the_file(history):
    history.add("something")
    history.clear()
    assert History(path=history.path).items == []


def test_a_read_only_home_does_not_bring_the_app_down(tmp_path):
    blocked = History(path=tmp_path / "nope" / "h.json")
    blocked.path.parent.mkdir()
    blocked.path.parent.chmod(0o500)
    try:
        blocked.add("still fine")     # save may fail; adding must not raise
    finally:
        blocked.path.parent.chmod(0o700)
    assert blocked.items == ["still fine"]


# ----------------------------------------------------------------- clipboard
def test_paths_with_spaces_are_quoted():
    assert quote(r"C:\Program Files\a.txt") == '"C:\\Program Files\\a.txt"'
    assert quote(r"C:\tmp\a.txt") == r"C:\tmp\a.txt"


def test_nothing_is_read_off_windows(monkeypatch):
    monkeypatch.setattr(recall, "IS_WINDOWS", False)
    assert recall.clipboard_files() == []


def test_only_the_file_list_format_is_ever_asked_for():
    """CF_HDROP and nothing else - copied text stays where the user left it."""
    source = (recall.__file__)
    with open(source, encoding="utf-8") as handle:
        text = handle.read()
    assert "CF_HDROP" in text
    for text_format in ("CF_TEXT", "CF_UNICODETEXT", "GetClipboardData(1)", "(13)"):
        assert text_format not in text, text_format


def test_the_bar_offers_copied_files_as_a_ready_made_argument(config, monkeypatch):
    monkeypatch.setattr(recall, "clipboard_files",
                        lambda: [r"C:\notes\plan.md", r"C:\my files\a.txt"])
    api = Api(config)
    api.new_tab()
    offered = api.clipboard_paths()

    assert offered["paths"][0] == r"C:\notes\plan.md"
    assert '"C:\\my files\\a.txt"' in offered["text"]      # the spaced one is quoted


def test_an_empty_clipboard_offers_nothing(config, monkeypatch):
    monkeypatch.setattr(recall, "clipboard_files", lambda: [])
    api = Api(config)
    api.new_tab()
    assert api.clipboard_paths() == {"paths": [], "text": ""}


# -------------------------------------------------------- a pasted picture
def _png() -> str:
    pytest.importorskip("PIL")
    from PIL import Image

    buffer = io.BytesIO()
    Image.new("RGB", (8, 6), (30, 30, 40)).save(buffer, format="PNG")
    return "data:image/png;base64," + base64.b64encode(buffer.getvalue()).decode()


class _Eyes:
    def __init__(self, answer="a bar chart of monthly revenue"):
        self.answer = answer
        self.seen = 0

    def vision(self, prompt, image_b64, model=None):
        self.seen += 1
        return "  " + self.answer + "  "


def _api_with_eyes(config, provider):
    api = Api(config)
    tab = api.new_tab()
    api.sessions[tab["id"]].agent.provider = provider
    return api


def test_a_pasted_picture_comes_back_as_words(config):
    eyes = _Eyes()
    api = _api_with_eyes(config, eyes)

    result = api.describe_image(_png())
    assert result["text"] == "a bar chart of monthly revenue"
    assert eyes.seen == 1


def test_something_that_is_not_a_picture_is_refused(config):
    api = _api_with_eyes(config, _Eyes())
    assert "error" in api.describe_image("not an image at all")


def test_a_broken_picture_is_reported_rather_than_swallowed(config):
    api = _api_with_eyes(config, _Eyes())
    rubbish = "data:image/png;base64," + base64.b64encode(b"nope").decode()
    assert "could not read" in api.describe_image(rubbish)["error"]


def test_a_vision_model_that_is_down_is_reported(config):
    class Broken:
        def vision(self, *args, **kwargs):
            raise RuntimeError("vision model is down")

    api = _api_with_eyes(config, Broken())
    assert "vision model is down" in api.describe_image(_png())["error"]


# ----------------------------------------------------------- the bar's memory
def test_sending_a_message_is_remembered(config, tmp_path, monkeypatch):
    api = Api(config)
    api.history = History(path=tmp_path / "h.json")
    tab = api.new_tab()["id"]
    monkeypatch.setattr(api.sessions[tab], "send_message", lambda text: None)
    monkeypatch.setattr(api, "expand", lambda: None)

    api.send(tab, "open my downloads folder")
    assert api.history.items == ["open my downloads folder"]
    assert api.state()["history"] == ["open my downloads folder"]


def test_a_message_that_could_not_be_sent_is_not_remembered(config, tmp_path):
    api = Api(config)
    api.history = History(path=tmp_path / "h.json")
    tab = api.new_tab()["id"]
    api.sessions[tab].busy = True

    assert "error" in api.send(tab, "do the thing")
    assert api.history.items == []
