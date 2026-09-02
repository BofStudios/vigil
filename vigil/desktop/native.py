"""Native window polish, per platform.

Windows 11 gets its rounded corners and dark frame from DWM. macOS gets its
vibrancy from pywebview itself (see app.py), and its corners from the system.
Everything here degrades quietly: on an older Windows, on Linux, or anywhere the
API is missing, the calls do nothing and the CSS carries the look on its own.
"""

from __future__ import annotations

import ctypes
import platform
import sys
from ctypes import wintypes

IS_WINDOWS = platform.system() == "Windows"
IS_MAC = platform.system() == "Darwin"

# DwmSetWindowAttribute attributes
DWMWA_USE_IMMERSIVE_DARK_MODE = 20
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWA_SYSTEMBACKDROP_TYPE = 38

# DWM_WINDOW_CORNER_PREFERENCE
CORNER_DEFAULT = 0
CORNER_DO_NOT_ROUND = 1
CORNER_ROUND = 2
CORNER_ROUND_SMALL = 3

# DWM_SYSTEMBACKDROP_TYPE
BACKDROP_AUTO = 0
BACKDROP_NONE = 1
BACKDROP_MICA = 2
BACKDROP_ACRYLIC = 3
BACKDROP_MICA_ALT = 4

# SetWindowPos
HWND_TOPMOST = -1
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001
SWP_NOACTIVATE = 0x0010
SWP_SHOWWINDOW = 0x0040


class _Margins(ctypes.Structure):
    _fields_ = [
        ("cxLeftWidth", ctypes.c_int),
        ("cxRightWidth", ctypes.c_int),
        ("cyTopHeight", ctypes.c_int),
        ("cyBottomHeight", ctypes.c_int),
    ]


def _windows_build() -> int:
    if not IS_WINDOWS:
        return 0
    try:
        return sys.getwindowsversion().build
    except Exception:
        return 0


def supports_acrylic() -> bool:
    """DWMWA_SYSTEMBACKDROP_TYPE landed in Windows 11 22H2 (build 22621)."""
    return _windows_build() >= 22621


def supports_rounding() -> bool:
    """Rounded corners came with the first Windows 11 build."""
    return _windows_build() >= 22000


def find_window(title: str, visible_only: bool = True):
    """Find a top-level window by exact title. Returns an HWND or None.

    A toolkit can leave short-lived helper windows under the same title, and
    holding on to one of those means every later call against the handle fails
    once it is destroyed. Preferring a visible window avoids that.
    """
    if not IS_WINDOWS:
        return None
    try:
        user32 = ctypes.windll.user32
        handle = user32.FindWindowW(None, title)
        if not handle:
            return None
        if visible_only and not user32.IsWindowVisible(wintypes.HWND(handle)):
            # walk the rest of the windows with this title
            found = []

            @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
            def collect(hwnd, _param):
                length = user32.GetWindowTextLengthW(hwnd)
                if length:
                    buffer = ctypes.create_unicode_buffer(length + 1)
                    user32.GetWindowTextW(hwnd, buffer, length + 1)
                    if buffer.value == title and user32.IsWindowVisible(hwnd):
                        found.append(hwnd)
                        return False
                return True

            user32.EnumWindows(collect, 0)
            if found:
                return found[0]
        return handle
    except Exception:
        return None


def _set_attribute(hwnd, attribute: int, value: int) -> bool:
    try:
        result = ctypes.windll.dwmapi.DwmSetWindowAttribute(
            wintypes.HWND(hwnd),
            ctypes.c_uint(attribute),
            ctypes.byref(ctypes.c_int(value)),
            ctypes.sizeof(ctypes.c_int),
        )
        return result == 0
    except Exception:
        return False


def apply_glass(hwnd, backdrop: int = BACKDROP_ACRYLIC, rounded: bool = True) -> dict:
    """Turn a plain window into a piece of glass. Returns what actually took effect."""
    applied = {"dark": False, "rounded": False, "backdrop": False}
    if not IS_WINDOWS or not hwnd:
        return applied

    applied["dark"] = _set_attribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1)

    if rounded and supports_rounding():
        applied["rounded"] = _set_attribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, CORNER_ROUND)

    if supports_acrylic():
        # The frame has to be extended into the client area first, otherwise the
        # backdrop has nothing to paint onto.
        try:
            margins = _Margins(-1, -1, -1, -1)
            ctypes.windll.dwmapi.DwmExtendFrameIntoClientArea(
                wintypes.HWND(hwnd), ctypes.byref(margins)
            )
        except Exception:
            pass
        applied["backdrop"] = _set_attribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, backdrop)

    return applied


def set_topmost(hwnd, topmost: bool = True) -> bool:
    """Keep the window above everything else, without stealing focus."""
    if not IS_WINDOWS or not hwnd:
        return False
    try:
        return bool(
            ctypes.windll.user32.SetWindowPos(
                wintypes.HWND(hwnd),
                wintypes.HWND(HWND_TOPMOST if topmost else -2),
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            )
        )
    except Exception:
        return False


def screen_size() -> tuple:
    """Primary screen size in pixels, or a sane default."""
    if IS_WINDOWS:
        try:
            user32 = ctypes.windll.user32
            user32.SetProcessDPIAware()
            return user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
        except Exception:
            pass
    try:
        import mss

        with mss.mss() as sct:
            monitor = sct.monitors[1] if len(sct.monitors) > 1 else sct.monitors[0]
            return monitor["width"], monitor["height"]
    except Exception:
        return 1920, 1080


def monitors() -> list:
    """Every display, as (left, top, width, height) in virtual-desktop pixels.

    Coordinates can be negative: a monitor placed to the left of the primary one
    starts below zero. Anything drawn across the whole desktop has to work in
    this space rather than assuming the primary screen is all there is.
    """
    if IS_WINDOWS:
        try:
            user32 = ctypes.windll.user32
            user32.SetProcessDPIAware()
            found = []

            proc = ctypes.WINFUNCTYPE(
                ctypes.c_int, wintypes.HMONITOR, wintypes.HDC,
                ctypes.POINTER(wintypes.RECT), wintypes.LPARAM,
            )

            def collect(_monitor, _dc, rect, _param):
                box = rect.contents
                found.append((box.left, box.top,
                              box.right - box.left, box.bottom - box.top))
                return 1

            user32.EnumDisplayMonitors(None, None, proc(collect), 0)
            if found:
                return found
        except Exception:
            pass
    try:
        import mss

        with mss.mss() as sct:
            # index 0 is the bounding box of them all; the rest are real screens
            screens = sct.monitors[1:] or sct.monitors
            return [(m["left"], m["top"], m["width"], m["height"]) for m in screens]
    except Exception:
        width, height = screen_size()
        return [(0, 0, width, height)]


def virtual_screen() -> tuple:
    """(left, top, width, height) covering every display at once."""
    if IS_WINDOWS:
        try:
            metrics = ctypes.windll.user32.GetSystemMetrics
            ctypes.windll.user32.SetProcessDPIAware()
            # SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN
            left, top = metrics(76), metrics(77)
            width, height = metrics(78), metrics(79)
            if width > 0 and height > 0:
                return left, top, width, height
        except Exception:
            pass

    boxes = monitors()
    left = min(box[0] for box in boxes)
    top = min(box[1] for box in boxes)
    right = max(box[0] + box[2] for box in boxes)
    bottom = max(box[1] + box[3] for box in boxes)
    return left, top, right - left, bottom - top


def cursor_position():
    """Where the pointer is, in screen pixels. None when it cannot be read."""
    if IS_WINDOWS:
        try:
            point = wintypes.POINT()
            if ctypes.windll.user32.GetCursorPos(ctypes.byref(point)):
                return point.x, point.y
        except Exception:
            return None
        return None
    try:
        import pyautogui

        position = pyautogui.position()
        return int(position.x), int(position.y)
    except Exception:
        return None


def window_rect(hwnd):
    """(left, top, right, bottom) of a window, or None."""
    if not IS_WINDOWS or not hwnd:
        return None
    try:
        box = wintypes.RECT()
        if ctypes.windll.user32.GetWindowRect(wintypes.HWND(hwnd), ctypes.byref(box)):
            return box.left, box.top, box.right, box.bottom
    except Exception:
        return None
    return None


def flash_focus(hwnd) -> None:
    """Bring the window forward and give it the keyboard, even from the tray."""
    if not IS_WINDOWS or not hwnd:
        return
    try:
        user32 = ctypes.windll.user32
        user32.ShowWindow(wintypes.HWND(hwnd), 9)  # SW_RESTORE
        user32.SetForegroundWindow(wintypes.HWND(hwnd))
    except Exception:
        pass


# ---------------------------------------------------------------- hot keys
MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_NOREPEAT = 0x4000
WM_HOTKEY = 0x0312
VK_SPACE = 0x20
VK_A = 0x41


class HotKey:
    """A global hot key, on its own thread with its own message loop.

    RegisterHotKey delivers WM_HOTKEY to the thread that registered it, so the
    loop has to live where the registration happened.
    """

    def __init__(self, callback, modifiers: int = MOD_CONTROL | MOD_SHIFT,
                 key: int = VK_SPACE, combo: str = "<ctrl>+<shift>+<space>"):
        self.callback = callback
        self.combo = combo  # the same keys again, spelled the way pynput wants
        self.modifiers = modifiers | MOD_NOREPEAT
        self.key = key
        self.registered = False
        self._thread = None
        self._thread_id = None
        self._listener = None

    def start(self) -> bool:
        import threading

        if not IS_WINDOWS:
            return self._start_pynput()

        ready = threading.Event()
        self._thread = threading.Thread(target=self._loop, args=(ready,), daemon=True)
        self._thread.start()
        ready.wait(timeout=2)
        return self.registered

    def _start_pynput(self) -> bool:
        """macOS and Linux have no RegisterHotKey; pynput covers them when installed.

        It is not a dependency - without it the tray and the shortcut are still
        there, you just do not get the summon key.
        """
        try:
            from pynput import keyboard
        except ImportError:
            return False

        try:
            self._listener = keyboard.GlobalHotKeys({self.combo: self.callback})
            self._listener.daemon = True
            self._listener.start()
            self.registered = True
        except Exception:
            self.registered = False
        return self.registered

    def _loop(self, ready) -> None:
        try:
            user32 = ctypes.windll.user32
            self._thread_id = ctypes.windll.kernel32.GetCurrentThreadId()
            self.registered = bool(user32.RegisterHotKey(None, 1, self.modifiers, self.key))
        except Exception:
            self.registered = False
        finally:
            ready.set()

        if not self.registered:
            return

        message = wintypes.MSG()
        try:
            while ctypes.windll.user32.GetMessageW(ctypes.byref(message), None, 0, 0) > 0:
                if message.message == WM_HOTKEY:
                    try:
                        self.callback()
                    except Exception:
                        pass
        except Exception:
            pass

    def stop(self) -> None:
        if self._listener is not None:
            try:
                self._listener.stop()
            except Exception:
                pass
            self._listener = None
            self.registered = False
            return
        if not IS_WINDOWS or not self.registered:
            return
        try:
            ctypes.windll.user32.UnregisterHotKey(None, 1)
            if self._thread_id:
                ctypes.windll.user32.PostThreadMessageW(self._thread_id, 0x0012, 0, 0)  # WM_QUIT
        except Exception:
            pass
        self.registered = False
