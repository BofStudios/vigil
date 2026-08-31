"""Render a screenshot of the desktop UI for the README.

The page is the real front end; only the Python bridge is stubbed, so what you
see is what the app draws. Run after changing the UI:

    python tools/make_screenshot.py
"""

from __future__ import annotations

import http.server
import socketserver
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "vigil" / "desktop" / "web"
OUT = ROOT / "docs" / "screenshot.png"
OUT_APPROVAL = ROOT / "docs" / "approval.png"
PORT = 8911

MOCK = """
(() => {
  const send = (p) => setTimeout(() => window.vigil.receive(p), p._at || 0);
  window.pywebview = { api: {
    ready: async () => ({
      version: "0.3.0", provider: "groq", model: "openai/gpt-oss-120b", mode: "ask", warning: "",
      tabs: [
        { id: "tab-1", title: "Tidy up the downloads folder", cwd: "C:\\\\Users\\\\you\\\\Downloads", busy: true, tools: 44 },
        { id: "tab-2", title: "Release notes", cwd: "C:\\\\Code\\\\vigil", busy: false, tools: 44 },
      ],
    }),
    new_tab: async () => ({ id: "tab-3", title: "New session", cwd: "C:\\\\", tools: 44 }),
    close_tab: async () => ({ tabs: [] }), send: async () => ({ ok: true }),
    stop: async () => ({ ok: true }), answer: async () => ({ ok: true }),
    set_mode: async (m) => ({ mode: m }), set_model: async (m) => ({ model: m }),
    models: async () => ({ models: [] }), tools: async () => ({ groups: {} }),
    pick_folder: async () => ({ cancelled: true }),
    minimize(){}, toggle_maximize(){}, close(){},
  }};
  window.addEventListener("load", () => setTimeout(() => {
    const tab = "tab-1";
    send({ tab, type: "user", text: "Tidy up my downloads folder and tell me what is safe to delete." });
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
      "Here is what I found in **Downloads**:\\n\\n- 142 files, **1.9 GB** total\\n- I moved 8 installers into `installers/`\\n- Two files dominate the folder:\\n\\n```\\narchive.zip     212 MB\\nold_backup.7z   1.4 GB\\n```\\n\\n`old_backup.7z` has not been touched since March. Want me to delete it?" });
    if (location.search.includes("approval")) {
      send({ _at: 80, tab, type: "approval", request: "req-1", tool: "delete_path",
        summary: "FILE TO DELETE: C:\\\\Users\\\\you\\\\Downloads\\\\old_backup.7z (1.4 GB)",
        reason: "permanent deletion", risk: "high",
        detail: "FILE TO DELETE: C:\\\\Users\\\\you\\\\Downloads\\\\old_backup.7z (1.4 GB)\\nLast modified: 2026-03-11" });
    }
  }, 120));
})();
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

    try:
        OUT.parent.mkdir(parents=True, exist_ok=True)
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch()
            page = browser.new_context(
                viewport={"width": 1180, "height": 760}, device_scale_factor=2
            ).new_page()
            base = "http://127.0.0.1:" + str(PORT) + "/__preview.html"
            for path, query in ((OUT, ""), (OUT_APPROVAL, "?approval=1")):
                page.goto(base + query)
                page.wait_for_timeout(1400)
                page.screenshot(path=str(path))
                print("wrote", path)
            browser.close()
    finally:
        server.shutdown()
        mock.unlink(missing_ok=True)
        preview.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
