"""What you asked the bar before, and what Windows has on the clipboard.

Two small things that make the bar feel like it belongs on the machine rather
than floating above it: pressing Up gets back what you typed last time, and
copying a file in Explorer and pasting it here gives Vigil the path.

The clipboard side is read-only and reaches for one format only - the list of
files Explorer puts there when you copy. It never reads text you have copied,
because that is almost always something you did not mean to hand to an agent.
"""

from __future__ import annotations

import ctypes
import json
import platform
from ctypes import wintypes

from ..config import VIGIL_HOME

IS_WINDOWS = platform.system() == "Windows"

HISTORY_FILE = VIGIL_HOME / "bar-history.json"
KEEP = 200          # how many past prompts are worth carrying around
CF_HDROP = 15


class History:
    """The prompts you have sent from the bar, newest last."""

    def __init__(self, path=None, keep: int = KEEP):
        self.path = path or HISTORY_FILE
        self.keep = keep
        self.items: list = []
        self.load()

    def load(self) -> list:
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            raw = []
        if not isinstance(raw, list):
            raw = []
        # only real strings: a malformed file should not put the word "None"
        # into someone's history
        self.items = [
            item for item in raw
            if isinstance(item, str) and item.strip()
        ][-self.keep:]
        return self.items

    def add(self, text: str) -> None:
        """Remember one prompt. Repeating yourself does not add a second copy."""
        cleaned = (text or "").strip()
        if not cleaned:
            return
        if self.items and self.items[-1] == cleaned:
            return
        # an older identical prompt moves to the end rather than being duplicated
        self.items = [item for item in self.items if item != cleaned]
        self.items.append(cleaned)
        del self.items[:-self.keep]
        self.save()

    def save(self) -> bool:
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps(self.items, ensure_ascii=False),
                                 encoding="utf-8")
            return True
        except OSError:
            return False

    def clear(self) -> None:
        self.items = []
        self.save()


def clipboard_files() -> list:
    """Paths of files copied in Explorer, or an empty list.

    Only CF_HDROP. Copied text is deliberately not read: the clipboard usually
    holds something personal, and nothing should send it anywhere by accident.
    """
    if not IS_WINDOWS:
        return []

    user32 = ctypes.windll.user32
    shell32 = ctypes.windll.shell32
    try:
        user32.OpenClipboard.argtypes = [wintypes.HWND]
        user32.GetClipboardData.argtypes = [wintypes.UINT]
        user32.GetClipboardData.restype = wintypes.HANDLE
        shell32.DragQueryFileW.argtypes = [wintypes.HANDLE, wintypes.UINT,
                                           wintypes.LPWSTR, wintypes.UINT]
        shell32.DragQueryFileW.restype = wintypes.UINT

        if not user32.IsClipboardFormatAvailable(CF_HDROP):
            return []
        if not user32.OpenClipboard(None):
            return []
    except Exception:
        return []

    try:
        handle = user32.GetClipboardData(CF_HDROP)
        if not handle:
            return []
        count = shell32.DragQueryFileW(handle, 0xFFFFFFFF, None, 0)
        found = []
        for index in range(min(count, 20)):
            length = shell32.DragQueryFileW(handle, index, None, 0)
            buffer = ctypes.create_unicode_buffer(length + 1)
            shell32.DragQueryFileW(handle, index, buffer, length + 1)
            if buffer.value:
                found.append(buffer.value)
        return found
    except Exception:
        return []
    finally:
        try:
            user32.CloseClipboard()
        except Exception:
            pass


def quote(path: str) -> str:
    """A path the model can read back unambiguously."""
    return '"' + path + '"' if " " in path else path
