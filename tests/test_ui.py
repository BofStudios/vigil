"""The front end.

Two layers. The first reads the stylesheet and the script as text and runs
anywhere, including CI, which installs no browser. The second drives the real
page in Chromium and is skipped when Playwright is not installed - it is what
catches a motion effect that leaves noise on screen or throws in the console.
"""

import sys
from pathlib import Path

import pytest

WEB = Path(__file__).resolve().parent.parent / "vigil" / "desktop" / "web"
TOOLS = Path(__file__).resolve().parent.parent / "tools"

CSS = (WEB / "style.css").read_text(encoding="utf-8")
JS = (WEB / "app.js").read_text(encoding="utf-8")
HTML = (WEB / "index.html").read_text(encoding="utf-8")


# ------------------------------------------------------------------ static
def test_the_front_end_loads_nothing_from_the_network():
    """No CDN, no font host, no analytics. It runs with the machine offline.

    Handing a URL to Python to open in the real browser is a different thing and
    is allowed - that is how the setup screen offers "get a free key".
    """
    for source in (CSS, JS, HTML):
        for banned in ("fetch(", "XMLHttpRequest", "WebSocket", "importScripts",
                       "//cdn", "@import url("):
            assert banned not in source, banned

    # nothing is fetched by the markup or the stylesheet either
    for source in (HTML, CSS):
        for attribute in ('src="http', "src='http", 'href="http', "href='http",
                          "url(http"):
            assert attribute not in source, attribute


def test_every_link_the_first_screen_offers_is_one_python_will_open():
    """A link the screen offers but the bridge refuses would be a dead button.

    The links live in routes.py rather than in the page, so this checks the two
    sides of that agree.
    """
    import webbrowser

    from vigil.config import Config
    from vigil.desktop.app import Api
    from vigil.routes import routes

    settings = Config()
    settings.api_key = "not-a-real-key"
    bridge = Api(settings)

    offered = [route["link"] for route in routes() if route["link"]]
    assert offered, "no route offers anywhere to get a key"

    opened = []
    real_open = webbrowser.open
    webbrowser.open = opened.append
    try:
        for url in offered:
            assert "error" not in bridge.open_url(url), url
    finally:
        webbrowser.open = real_open
    assert opened == offered


def test_the_page_itself_hard_codes_no_links():
    """Everything it can open comes from Python, so the two cannot drift."""
    import re

    assert re.findall(r"https?://[^\s\"')]+", JS) == []


def test_there_is_no_framework_hiding_in_here():
    assert "<script" in HTML
    assert HTML.count("<script") == 1     # app.js and nothing else


def test_movement_can_be_turned_off_by_the_system():
    assert "prefers-reduced-motion" in CSS
    assert "prefers-reduced-motion" in JS

    block = CSS[CSS.index("@media (prefers-reduced-motion: reduce)"):]
    for switched_off in (".msg", ".tool", ".plan-item", ".mark .trace",
                         ".bar::after", ".round"):
        assert switched_off in block, switched_off


def test_the_mark_has_a_stroke_for_the_light_to_travel_along():
    assert 'class="trace"' in HTML
    assert 'pathLength="100"' in HTML     # so the dash is written in percent
    assert "@keyframes trace" in CSS


def test_the_resting_capsule_says_nothing_at_all():
    """It sits on screen all day. It should not be talking while it waits."""
    start = CSS.index("resting */")
    resting = CSS[start:CSS.index("voice */", start)]

    # the name, the input, the buttons and the mode dot are all put away
    assert ".resting .pill-hint" in resting
    assert "display: none" in resting
    assert "border-radius: 999px" in resting       # a capsule, not a box
    assert "@keyframes sheen" not in CSS           # and no light sweeps across it


def test_nothing_in_the_motion_layer_changes_what_anything_does():
    """These helpers may only set custom properties and class names."""
    motion = JS[JS.index("function glare("):JS.index("function renderMarkdown(")]
    for forbidden in ("api()", "innerHTML", "location", "eval("):
        assert forbidden not in motion, forbidden


# ----------------------------------------------------------------- in a browser
def _preview():
    """Write the mock harness the screenshot tool already uses."""
    sys.path.insert(0, str(TOOLS))
    import make_screenshot as harness

    (WEB / "__mock.js").write_text(harness.MOCK, encoding="utf-8")
    (WEB / "__preview.html").write_text(
        HTML.replace(
            '<script src="app.js"></script>',
            '<script src="__mock.js"></script>\n  <script src="app.js"></script>',
        ),
        encoding="utf-8",
    )
    return harness


@pytest.fixture
def page():
    pytest.importorskip("playwright")
    import socketserver
    import threading

    from playwright.sync_api import sync_playwright

    harness = _preview()
    server = socketserver.TCPServer(("127.0.0.1", 0), harness.Handler)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()

    made = {}

    def open_page(reduced_motion="no-preference", query=""):
        playwright = sync_playwright().start()
        try:
            browser = playwright.chromium.launch()
        except Exception as exc:                       # no chromium installed
            playwright.stop()
            pytest.skip("chromium is not installed: " + str(exc))
        context = browser.new_context(viewport={"width": 760, "height": 620},
                                      reduced_motion=reduced_motion)
        opened = context.new_page()
        made["playwright"] = playwright
        made["browser"] = browser
        opened.problems = []
        opened.on("console",
                  lambda m: opened.problems.append((m.type, m.text))
                  if m.type == "error" else None)
        opened.on("pageerror", lambda e: opened.problems.append(("pageerror", str(e))))
        opened.goto("http://127.0.0.1:" + str(port) + "/__preview.html" + query)
        opened.wait_for_timeout(900)
        return opened

    yield open_page

    if "browser" in made:
        made["browser"].close()
        made["playwright"].stop()
    server.shutdown()
    (WEB / "__mock.js").unlink(missing_ok=True)
    (WEB / "__preview.html").unlink(missing_ok=True)


def test_the_page_loads_without_complaining(page):
    opened = page()
    assert opened.problems == []


def test_the_scramble_settles_on_the_real_name(page):
    """A resolve effect that does not resolve would leave noise on screen."""
    opened = page()
    opened.wait_for_timeout(600)
    names = opened.eval_on_selector_all(".tool .name", "els => els.map(e => e.textContent)")
    assert names, "no tool rows were rendered"
    assert all(name in ("list_dir", "make_dir") for name in names), names
    assert opened.eval_on_selector_all(".tool .name.settling", "els => els.length") == 0


def test_the_highlight_follows_the_pointer_and_leaves_with_it(page):
    opened = page()
    read = "getComputedStyle(document.getElementById('bar')).getPropertyValue('--glare').trim()"

    opened.mouse.move(300, 34)
    opened.wait_for_timeout(200)
    assert opened.evaluate(read) == "1"

    opened.mouse.move(300, 600)
    opened.wait_for_timeout(200)
    assert opened.evaluate(read) == "0"


def test_a_control_leans_towards_the_pointer(page):
    opened = page()
    middle = opened.eval_on_selector(
        "#send", "e => { const b = e.getBoundingClientRect(); return [b.left + b.width/2, b.top + b.height/2]; }"
    )
    opened.mouse.move(middle[0] - 26, middle[1])
    opened.wait_for_timeout(200)

    pull = opened.eval_on_selector("#send", "e => e.style.getPropertyValue('--pull-x')")
    assert pull.endswith("px")
    assert -12 < float(pull[:-2]) < 0      # towards the pointer, and only a little


def test_asking_for_less_movement_turns_all_of_it_off(page):
    opened = page(reduced_motion="reduce")
    opened.wait_for_timeout(500)

    assert opened.problems == []
    # the light on the mark is not drawn at all
    assert opened.eval_on_selector(
        ".mark .trace", "e => getComputedStyle(e).opacity"
    ) == "0"

    opened.mouse.move(300, 34)
    opened.wait_for_timeout(200)
    assert opened.evaluate(
        "getComputedStyle(document.getElementById('bar')).getPropertyValue('--glare').trim()"
    ) in ("", "0")
    assert opened.eval_on_selector("#send", "e => e.style.getPropertyValue('--pull-x')") == ""


# --------------------------------------------------------- recall and paste
def test_up_walks_back_through_what_you_asked(page):
    opened = page()
    opened.click("#input")

    opened.keyboard.press("ArrowUp")
    opened.wait_for_timeout(80)
    assert opened.input_value("#input") == "sort my screenshots by month"

    opened.keyboard.press("ArrowUp")
    opened.wait_for_timeout(80)
    assert opened.input_value("#input") == "open my downloads folder"


def test_walking_forward_again_gives_back_what_you_were_typing(page):
    opened = page()
    opened.click("#input")
    opened.keyboard.type("half a thought")

    opened.keyboard.press("ArrowUp")
    opened.wait_for_timeout(80)
    assert opened.input_value("#input") == "sort my screenshots by month"

    opened.keyboard.press("ArrowDown")
    opened.wait_for_timeout(80)
    assert opened.input_value("#input") == "half a thought"


def test_the_oldest_prompt_is_where_it_stops(page):
    opened = page()
    opened.click("#input")
    for _ in range(6):
        opened.keyboard.press("ArrowUp")
        opened.wait_for_timeout(40)
    assert opened.input_value("#input") == "open my downloads folder"


def test_arrows_still_move_the_caret_in_a_prompt_of_several_lines(page):
    """Recall must not take the arrow keys away from ordinary editing."""
    opened = page()
    opened.click("#input")
    opened.keyboard.type("first line")
    opened.keyboard.down("Shift")
    opened.keyboard.press("Enter")
    opened.keyboard.up("Shift")
    opened.keyboard.type("second line")

    opened.keyboard.press("ArrowUp")
    opened.wait_for_timeout(80)
    assert "first line" in opened.input_value("#input")
    assert "second line" in opened.input_value("#input")


def test_a_pasted_picture_arrives_as_words(page):
    opened = page()
    opened.click("#input")

    # a one-pixel PNG dropped onto the composer the way a paste delivers one
    opened.evaluate("""() => {
      const png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
      const bytes = Uint8Array.from(atob(png), c => c.charCodeAt(0));
      const file = new File([bytes], 'shot.png', { type: 'image/png' });
      const data = new DataTransfer();
      data.items.add(file);
      document.getElementById('input').dispatchEvent(
        new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    }""")
    opened.wait_for_timeout(700)
    assert "bar chart of monthly revenue" in opened.input_value("#input")


def test_pasting_with_a_file_copied_in_explorer_gives_the_path(page):
    opened = page()
    opened.click("#input")

    opened.evaluate("""() => {
      const data = new DataTransfer();     // no text, no files: what Windows does
      document.getElementById('input').dispatchEvent(
        new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    }""")
    opened.wait_for_timeout(400)
    assert "C:/notes/plan.md" in opened.input_value("#input")


def test_ordinary_copied_text_is_pasted_as_text(page):
    """Nothing clever: if there is text on the clipboard it goes in unchanged."""
    opened = page()
    opened.click("#input")

    opened.evaluate("""() => {
      const data = new DataTransfer();
      data.setData('text/plain', 'just some words');
      document.getElementById('input').dispatchEvent(
        new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    }""")
    opened.wait_for_timeout(300)
    # the browser performs the insertion itself; what matters is that nothing
    # replaced it with a path or a description
    assert "C:/notes/plan.md" not in opened.input_value("#input")
    assert "bar chart" not in opened.input_value("#input")


# ------------------------------------------------------------- first run
def test_a_fresh_install_shows_the_setup_screen(page):
    opened = page(query="?setup=1")
    opened.wait_for_timeout(500)

    assert opened.is_visible("#setup")
    assert "Enter your API key" in opened.text_content(".setup-title")
    assert opened.problems == []


def test_the_bar_gets_out_of_the_way_until_it_can_think(page):
    """Typing into a box that cannot send anything is a dead end."""
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    assert opened.eval_on_selector("#shell", "e => e.classList.contains('needs-setup')")
    assert not opened.is_visible("#input")


def test_the_free_route_names_the_models_it_gives_you(page):
    """Nobody should have to guess what they just signed up to."""
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    listed = opened.eval_on_selector_all(
        "#setup-models .id", "els => els.map(e => e.textContent)")
    assert listed == ["openai/gpt-oss-20b", "openai/gpt-oss-120b"]


def test_you_can_bring_your_own_claude(page):
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    opened.click('.setup-tab[data-route="anthropic"]')
    opened.wait_for_timeout(200)

    assert "Claude" in opened.text_content("#setup-sub") or         "Anthropic" in opened.text_content("#setup-sub")
    assert opened.eval_on_selector("#setup-key", "e => e.placeholder").startswith("sk-ant")
    listed = opened.eval_on_selector_all(
        "#setup-models .id", "els => els.map(e => e.textContent)")
    assert "claude-sonnet-5" in listed


def test_you_can_choose_to_stay_offline_instead(page):
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    opened.click('.setup-tab[data-route="ollama"]')
    opened.wait_for_timeout(200)

    assert "nothing is sent anywhere" in opened.text_content("#setup-sub").lower()
    # the offline route takes an address, not a secret, so it is not masked
    assert opened.eval_on_selector("#setup-key", "e => e.type") == "text"


def test_switching_route_clears_whatever_was_typed(page):
    """A key pasted for one provider must not be sent to another."""
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    opened.fill("#setup-key", "gsk_secret")
    opened.click('.setup-tab[data-route="anthropic"]')
    opened.wait_for_timeout(200)
    assert opened.input_value("#setup-key") == ""


def test_a_key_that_works_puts_the_screen_away(page):
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    opened.fill("#setup-key", "gsk_a_key_that_works")
    opened.click("#setup-go")
    opened.wait_for_timeout(600)

    assert not opened.is_visible("#setup")
    assert opened.is_visible("#input")
    assert opened.problems == []


def test_a_key_that_is_refused_says_why_and_stays_put(page):
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    opened.fill("#setup-key", "bad")
    opened.click("#setup-go")
    opened.wait_for_timeout(500)

    assert opened.is_visible("#setup")
    assert "not accepted" in opened.text_content("#setup-error")
    # and the button comes back rather than staying stuck on "Checking…"
    assert opened.text_content("#setup-go").strip() == "Connect"
    assert opened.eval_on_selector("#setup-go", "e => e.disabled") is False


def test_enter_connects_too(page):
    opened = page(query="?setup=1")
    opened.wait_for_timeout(400)

    opened.fill("#setup-key", "gsk_works")
    opened.press("#setup-key", "Enter")
    opened.wait_for_timeout(600)
    assert not opened.is_visible("#setup")
