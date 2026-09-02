"""One Vigil at a time.

Running `vigil app` twice used to open a second bar over the first: two trays,
two hot keys fighting over the same combination, two sets of sessions. Now the
second one finds the first and asks it to come forward instead.

The handshake is a message-only window - a real window with no pixels, which
Windows keeps out of the taskbar and never draws. The running app owns one under
a known class name; a starting app looks for it, posts a message, and exits. No
files, no ports, and nothing left behind if the app is killed rather than closed.
"""

from __future__ import annotations

import ctypes
import platform
import threading
from ctypes import wintypes

IS_WINDOWS = platform.system() == "Windows"

CLASS_NAME = "VigilSingleInstance"
HWND_MESSAGE = -3
WM_APP = 0x8000
WM_SUMMON = WM_APP + 1      # "you are already running - show yourself"
WM_QUIT = 0x0012

if IS_WINDOWS:
    WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_long, wintypes.HWND, ctypes.c_uint,
                                 wintypes.WPARAM, wintypes.LPARAM)
else:
    WNDPROC = ctypes.c_void_p


class _WndClassEx(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint),
        ("style", ctypes.c_uint),
        ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", wintypes.HINSTANCE),
        ("hIcon", wintypes.HANDLE),
        ("hCursor", wintypes.HANDLE),
        ("hbrBackground", wintypes.HANDLE),
        ("lpszMenuName", wintypes.LPCWSTR),
        ("lpszClassName", wintypes.LPCWSTR),
        ("hIconSm", wintypes.HANDLE),
    ]


def _declare():
    if not IS_WINDOWS:
        return
    user32 = ctypes.windll.user32
    user32.FindWindowExW.argtypes = [wintypes.HWND, wintypes.HWND,
                                     wintypes.LPCWSTR, wintypes.LPCWSTR]
    user32.FindWindowExW.restype = wintypes.HWND
    user32.PostMessageW.argtypes = [wintypes.HWND, ctypes.c_uint,
                                    wintypes.WPARAM, wintypes.LPARAM]
    user32.CreateWindowExW.argtypes = [
        wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID,
    ]
    user32.CreateWindowExW.restype = wintypes.HWND
    user32.RegisterClassExW.argtypes = [ctypes.c_void_p]
    user32.RegisterClassExW.restype = wintypes.ATOM
    user32.DefWindowProcW.argtypes = [wintypes.HWND, ctypes.c_uint,
                                      wintypes.WPARAM, wintypes.LPARAM]
    user32.DefWindowProcW.restype = ctypes.c_long
    user32.DestroyWindow.argtypes = [wintypes.HWND]
    ctypes.windll.kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
    ctypes.windll.kernel32.GetModuleHandleW.restype = wintypes.HMODULE


_declare()


def find_running():
    """The running Vigil's beacon window, or None."""
    if not IS_WINDOWS:
        return None
    try:
        handle = ctypes.windll.user32.FindWindowExW(
            wintypes.HWND(HWND_MESSAGE), None, CLASS_NAME, None
        )
        return handle or None
    except Exception:
        return None


def summon() -> bool:
    """Ask a running Vigil to come forward. False if there is not one."""
    handle = find_running()
    if not handle:
        return False
    try:
        ctypes.windll.user32.PostMessageW(wintypes.HWND(handle), WM_SUMMON, 0, 0)
        return True
    except Exception:
        return False


class Beacon:
    """Answers for this instance, so a second one can find it."""

    _proc = None
    _registered = False

    def __init__(self, on_summon=None):
        self.on_summon = on_summon or (lambda: None)
        self.listening = False
        self._hwnd = None
        self._thread = None
        self._ready = threading.Event()

    def start(self) -> bool:
        if not IS_WINDOWS:
            return False
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        self._ready.wait(timeout=3)
        return self.listening

    def stop(self) -> None:
        self.listening = False
        if self._hwnd:
            try:
                ctypes.windll.user32.PostMessageW(
                    wintypes.HWND(self._hwnd), WM_QUIT, 0, 0
                )
            except Exception:
                pass

    def _register(self) -> bool:
        if Beacon._registered:
            return True
        try:
            def proc(hwnd, message, wparam, lparam):
                if message == WM_SUMMON:
                    try:
                        self.on_summon()
                    except Exception:
                        pass
                    return 0
                return ctypes.windll.user32.DefWindowProcW(hwnd, message, wparam, lparam)

            Beacon._proc = WNDPROC(proc)     # must outlive the registration
            cls = _WndClassEx()
            cls.cbSize = ctypes.sizeof(_WndClassEx)
            cls.lpfnWndProc = Beacon._proc
            cls.hInstance = ctypes.windll.kernel32.GetModuleHandleW(None)
            cls.lpszClassName = CLASS_NAME
            if not ctypes.windll.user32.RegisterClassExW(ctypes.byref(cls)):
                return False
            Beacon._registered = True
            return True
        except Exception:
            return False

    def _run(self) -> None:
        try:
            if not self._register():
                self._ready.set()
                return
            user32 = ctypes.windll.user32
            self._hwnd = user32.CreateWindowExW(
                0, CLASS_NAME, "Vigil beacon", 0, 0, 0, 0, 0,
                wintypes.HWND(HWND_MESSAGE), None,
                ctypes.windll.kernel32.GetModuleHandleW(None), None,
            )
            if not self._hwnd:
                self._ready.set()
                return
            self.listening = True
            self._ready.set()

            # No TranslateMessage: this window has no keyboard input to translate.
            message = wintypes.MSG()
            while user32.GetMessageW(ctypes.byref(message), None, 0, 0) > 0:
                user32.DispatchMessageW(ctypes.byref(message))
        except Exception:
            self._ready.set()
        finally:
            self.listening = False
            if self._hwnd:
                try:
                    ctypes.windll.user32.DestroyWindow(wintypes.HWND(self._hwnd))
                except Exception:
                    pass
                self._hwnd = None
