"""Render screenshots of the bar for the README.

The page is the real front end; only the Python bridge is stubbed, so what you
see is what the app draws. The desktop wallpaper behind the real window shows
through the acrylic - here it is faked with a gradient so the glass reads.

    python tools/make_screenshot.py
"""

from __future__ import annotations

import http.server
import socketserver
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "vigil" / "desktop" / "web"
DOCS = ROOT / "docs"
PORT = 8911

PILL_WIDTH = 208
PILL_HEIGHT = 46
BAR_WIDTH = 720
BAR_HEIGHT = 68
PANEL_HEIGHT = 620

MOCK = """
(() => {
  const send = (p) => setTimeout(() => window.vigil.receive(p), p._at || 0);
  window.pywebview = { api: {
    ready: async () => ({
      version: "0.5.0", provider: "groq", model: "openai/gpt-oss-120b", mode: "ask",
      warning: "", hotkey: true, tray: true,
      // the panel shots show a plan, and only the autonomous brain writes one
      brain: "autonomous",
      history: ["open my downloads folder", "sort my screenshots by month"],
      setup: location.search.includes("setup")
        ? { needed: true, provider: "groq", reason: "", host: "http://localhost:11434" }
        : { needed: false, provider: "groq" },
      brains: [
        { key: "direct", name: "Direct", tagline: "Does what you ask",
          model: "openai/gpt-oss-20b", warning: "",
          summary: "Follows your instruction step by step and stops when it is finished. Best for everyday jobs - open this, rename that, find that file." },
        { key: "autonomous", name: "Autonomous", tagline: "Works out how",
          model: "openai/gpt-oss-120b",
          summary: "Give it a problem rather than an instruction and it plans a route, tries things, and changes course when they do not work. Best for when you do not know the steps yourself.",
          warning: "Takes many more actions on its own before it comes back to you. It still asks before touching your mouse, keyboard or screen - that never changes - but it will travel further on a wrong idea before you see it." },
      ],
      tabs: [{ id: "tab-1", title: "Tidy the downloads folder",
               cwd: "C:\\\\Users\\\\you\\\\Downloads", busy: false, tools: 45 }],
    }),
    new_tab: async () => ({ id: "tab-2", title: "New session", cwd: "C:\\\\", tools: 45 }),
    close_tab: async () => ({ tabs: [] }), send: async () => ({ ok: true }),
    stop: async () => ({ ok: true }), answer: async () => ({ ok: true }),
    set_mode: async (m) => ({ mode: m }), set_model: async (m) => ({ model: m }),
    set_brain: async (b) => ({ brain: b, model: b === "direct" ? "openai/gpt-oss-20b" : "openai/gpt-oss-120b" }),
    models: async () => ({ models: [] }), tools: async () => ({ groups: {} }),
    pick_folder: async () => ({ cancelled: true }), notify_done() {},
    clipboard_paths: async () => ({ paths: ["C:/notes/plan.md"], text: "C:/notes/plan.md" }),
    describe_image: async () => ({ text: "a bar chart of monthly revenue" }),
    hold() {},
    connect: async (provider, key) => (key === "bad"
      ? { error: "That key was not accepted by Groq." }
      : { ok: true, state: { mode: "ask", brain: "direct", brains: [], model: "openai/gpt-oss-20b",
                             history: [], setup: { needed: false }, tabs: [] } }),
    open_url: async () => ({ ok: true }),
    fit: async () => ({ ok: true }),
    expand() {}, collapse() {}, hide_window() {}, show_window() {},
  }};

  window.addEventListener("load", () => setTimeout(() => {
    if (location.search.includes("pill")) return;   // leave it resting
    if (location.search.includes("setup")) {
      document.getElementById("shell").classList.remove("resting");
      document.getElementById("shell").classList.add("expanded");
      return;
    }
    document.getElementById("shell").classList.remove("resting");
    if (location.search.includes("bar")) {
      document.getElementById("input").value = "sort my screenshots by month";
      document.getElementById("input").dispatchEvent(new Event("input"));
      return;
    }

    document.getElementById("shell").classList.add("expanded");
    const tab = "tab-1";
    send({ tab, type: "user", text: "Tidy my downloads folder and tell me what is safe to delete." });
    send({ _at: 10, tab, type: "plan", steps: [
      { text: "List everything in Downloads with sizes", status: "done", note: "142 files" },
      { text: "Group the installers into one folder", status: "done", note: "8 moved" },
      { text: "Find files older than six months", status: "doing", note: "" },
      { text: "Report what is safe to delete", status: "todo", note: "" },
    ]});
    send({ _at: 20, tab, type: "tool", name: "list_dir", summary: "list: C:\\\\Users\\\\you\\\\Downloads", risk: "safe" });
    send({ _at: 30, tab, type: "tool_result", ok: true, text:
      "C:\\\\Users\\\\you\\\\Downloads - 142 item(s)\\n[dir]  installers/\\n[file] archive.zip  212.0 MB\\n[file] old_backup.7z  1.4 GB" });
    send({ _at: 40, tab, type: "tool", name: "make_dir", summary: "mkdir: installers", risk: "moderate" });
    send({ _at: 50, tab, type: "tool_result", ok: true, text: "C:\\\\Users\\\\you\\\\Downloads\\\\installers is ready." });
    send({ _at: 60, tab, type: "assistant_full", text:
      "Here is what I found in **Downloads**:\\n\\n- 142 files, **1.9 GB** total\\n- I moved 8 installers into `installers/`\\n- `old_backup.7z` alone is **1.4 GB** and has not been touched since March\\n\\nWant me to delete it?" });

    if (location.search.includes("brains")) {
      setTimeout(() => document.getElementById("chip-brain").click(), 80);
      return;
    }

    if (location.search.includes("approval")) {
      send({ _at: 90, tab, type: "approval", request: "req-1", tool: "delete_path",
        summary: "FILE TO DELETE: C:\\\\Users\\\\you\\\\Downloads\\\\old_backup.7z (1.4 GB)",
        reason: "permanent deletion", risk: "high",
        detail: "FILE TO DELETE: C:\\\\Users\\\\you\\\\Downloads\\\\old_backup.7z (1.4 GB)\\nLast modified: 2026-03-11" });
    }
  }, 120));
})();
"""

# The real window floats over the desktop; a flat dark ground stands in for it
# so the shell reads as the solid, matte object it is.
BACKDROP = """
html { background: #14100e !important; }
body { padding: 0 !important; }
"""


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(WEB), **kwargs)

    def log_message(self, *args):
        pass


def main() -> None:
    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        raise SystemExit('needs playwright: pip install "vigil-cli[browser]"') from None

    mock = WEB / "__mock.js"
    preview = WEB / "__preview.html"
    mock.write_text(MOCK, encoding="utf-8")
    preview.write_text(
        (WEB / "index.html").read_text(encoding="utf-8").replace(
            '<script src="app.js"></script>',
            '<script src="__mock.js"></script>\n  <script src="app.js"></script>',
        ),
        encoding="utf-8",
    )

    server = socketserver.TCPServer(("127.0.0.1", PORT), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()

    shots = [
        ("screenshot.png", "", PANEL_HEIGHT),
        ("approval.png", "?approval=1", PANEL_HEIGHT),
        ("brains.png", "?brains=1", PANEL_HEIGHT),
        ("setup.png", "?setup=1", PANEL_HEIGHT),
        ("bar.png", "?bar=1", BAR_HEIGHT),
        ("pill.png", "?pill=1", PILL_HEIGHT),
    ]

    try:
        DOCS.mkdir(parents=True, exist_ok=True)
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(args=["--force-color-profile=srgb"])
            base = "http://127.0.0.1:" + str(PORT) + "/__preview.html"
            for name, query, height in shots:
                page = browser.new_context(
                    viewport={"width": BAR_WIDTH + 80, "height": height + 60},
                    device_scale_factor=2,
                ).new_page()
                page.goto(base + query)
                page.add_style_tag(content=BACKDROP)
                # centre the shell in the padded viewport so the shadow is visible
                page.add_style_tag(content=(
                    "body{display:grid;place-items:center;height:100vh}"
                    ".shell{width:" + str(PILL_WIDTH if name == "pill.png" else BAR_WIDTH)
                    + "px;height:" + str(height) + "px}"
                ))
                page.wait_for_timeout(1300)
                page.screenshot(path=str(DOCS / name))
                print("wrote", DOCS / name)
                page.close()
            browser.close()
    finally:
        server.shutdown()
        mock.unlink(missing_ok=True)
        preview.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
