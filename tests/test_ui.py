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
def test_the_front_end_still_reaches_no_network():
    """No CDN, no font host, no analytics. It runs with the machine offline."""
    for source in (CSS, JS, HTML):
        for banned in ("http://", "https://", "//cdn", "fetch(", "XMLHttpRequest"):
            assert banned not in source, banned


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


def test_the_moving_highlight_is_only_on_the_wordmark():
    """The surfaces stay matte; a reflection on lettering is a different thing."""
    sheen = CSS[CSS.index("@keyframes sheen"):]
    assert "background-position" in sheen
    assert "linear-gradient" not in sheen  # the gradient is on the text, not here


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

    def open_page(reduced_motion="no-preference"):
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
        opened.goto("http://127.0.0.1:" + str(port) + "/__preview.html")
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
