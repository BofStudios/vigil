"""A soft light around the edge of the screen, shown while Vigil has the controls.

Tk cannot do this. It has one opacity per window, so the best it can manage is a
few nested frames at different alphas - which is what this replaced, and it read
as grey bands rather than light.

A layered window can: `UpdateLayeredWindow` takes a 32-bit premultiplied bitmap
and honours the alpha of every pixel, so the glow is drawn once as an image and
handed to the compositor whole.

The shape matters as much as the technique. An even falloff across a wide band
reads as a grey vignette; light reads as a bright hairline with a short bloom
behind it. The frame is rounded, which is both nicer and avoids the diagonal
seam a square distance-to-edge produces in the corners.

One thing this cannot beat: a fullscreen exclusive game draws above every window
there is, topmost included.
"""

from __future__ import annotations

import ctypes
import math
import platform
import threading
import time
from contextlib import contextmanager
from ctypes import wintypes

IS_WINDOWS = platform.system() == "Windows"

# How the light behaves. A bright hairline hugging a rounded frame, with a short
# bloom behind it - a wide even falloff reads as a grey vignette, not as light.
CORE_ALPHA = 0.90     # the crisp line at the very edge
BLOOM_ALPHA = 0.34    # where the soft part starts
REACH = 30            # how far the bloom reaches inward, in pixels
DECAY = 9.0           # smaller keeps the light close to the edge
BLUR = 6              # softens the rings into one gradient
RADIUS = 20           # the frame is rounded, which also kills the corner seam
TINT = (255, 255, 255)

# --- Win32 ------------------------------------------------------------------
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_TOPMOST = 0x00000008
WS_EX_NOACTIVATE = 0x08000000
WS_POPUP = 0x80000000
SW_HIDE = 0
SW_SHOWNOACTIVATE = 4
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_NOACTIVATE = 0x0010
SWP_SHOWWINDOW = 0x0040
HWND_TOPMOST = -1
ULW_ALPHA = 0x00000002
AC_SRC_OVER = 0x00
AC_SRC_ALPHA = 0x01
BI_RGB = 0
DIB_RGB_COLORS = 0
WM_DESTROY = 0x0002
WM_QUIT = 0x0012


WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_long, wintypes.HWND, ctypes.c_uint,
                             wintypes.WPARAM, wintypes.LPARAM)


def _declare():
    """Give ctypes the real signatures.

    Guessed ones overflow here: WS_POPUP is 0x80000000, which does not fit the
    signed int ctypes assumes, and LPARAM is 64-bit on this platform.
    """
    if not IS_WINDOWS:
        return
    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32

    user32.DefWindowProcW.argtypes = [wintypes.HWND, ctypes.c_uint,
                                      wintypes.WPARAM, wintypes.LPARAM]
    user32.DefWindowProcW.restype = ctypes.c_long

    user32.CreateWindowExW.argtypes = [
        wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID,
    ]
    user32.CreateWindowExW.restype = wintypes.HWND

    user32.RegisterClassExW.argtypes = [ctypes.c_void_p]
    user32.RegisterClassExW.restype = wintypes.ATOM

    user32.UpdateLayeredWindow.argtypes = [
        wintypes.HWND, wintypes.HDC, ctypes.POINTER(wintypes.POINT),
        ctypes.POINTER(wintypes.SIZE), wintypes.HDC, ctypes.POINTER(wintypes.POINT),
        wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD,
    ]
    user32.UpdateLayeredWindow.restype = wintypes.BOOL

    user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
    user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int,
                                    ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                    wintypes.UINT]
    user32.SetWindowPos.restype = wintypes.BOOL
    user32.DestroyWindow.argtypes = [wintypes.HWND]
    user32.GetDC.argtypes = [wintypes.HWND]
    user32.GetDC.restype = wintypes.HDC
    user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]

    gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
    gdi32.CreateCompatibleDC.restype = wintypes.HDC
    gdi32.CreateDIBSection.argtypes = [
        wintypes.HDC, ctypes.c_void_p, wintypes.UINT,
        ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.DWORD,
    ]
    gdi32.CreateDIBSection.restype = wintypes.HBITMAP
    gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
    gdi32.SelectObject.restype = wintypes.HGDIOBJ
    gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
    gdi32.DeleteDC.argtypes = [wintypes.HDC]

    ctypes.windll.kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
    ctypes.windll.kernel32.GetModuleHandleW.restype = wintypes.HMODULE


_declare()


class _BlendFunction(ctypes.Structure):
    _fields_ = [
        ("BlendOp", ctypes.c_ubyte),
        ("BlendFlags", ctypes.c_ubyte),
        ("SourceConstantAlpha", ctypes.c_ubyte),
        ("AlphaFormat", ctypes.c_ubyte),
    ]


class _BitmapInfoHeader(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class _BitmapInfo(ctypes.Structure):
    _fields_ = [("bmiHeader", _BitmapInfoHeader), ("bmiColors", wintypes.DWORD * 3)]


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


def _alpha_at(distance: int) -> float:
    """How bright the light is this many pixels in from the edge."""
    if distance < 2:
        return CORE_ALPHA
    return BLOOM_ALPHA * math.exp(-(distance - 2) / DECAY)


def build_glow(width: int, height: int):
    """Draw the glow: rounded rings, blurred into one gradient, crisp line on top.

    Each ring is a single pixel wide and carries its own alpha, so there is no
    banding to begin with; the blur then removes the last of the stepping. The
    core line is redrawn afterwards so the blur cannot soften the edge itself.
    """
    from PIL import Image, ImageDraw, ImageFilter

    image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    limit = min(REACH, width // 2, height // 2)

    for distance in range(limit):
        alpha = _alpha_at(distance)
        if alpha <= 0.003:
            break
        draw.rounded_rectangle(
            [distance, distance, width - 1 - distance, height - 1 - distance],
            radius=max(2, RADIUS - distance),
            outline=TINT + (int(round(alpha * 255)),),
        )

    if BLUR:
        image = image.filter(ImageFilter.GaussianBlur(BLUR))
        core = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        ImageDraw.Draw(core).rounded_rectangle(
            [1, 1, width - 2, height - 2],
            radius=RADIUS,
            outline=TINT + (int(round(CORE_ALPHA * 255)),),
            width=2,
        )
        image = Image.alpha_composite(image, core)
    return image


def _premultiplied(image) -> bytes:
    """UpdateLayeredWindow wants BGRA with colour already multiplied by alpha.

    Done through PIL rather than a Python loop: at 2560x1440 that is 3.7 million
    pixels, and the loop took seconds where this takes milliseconds.
    """
    from PIL import Image, ImageChops

    red, green, blue, alpha = image.split()
    return Image.merge(
        "RGBA",
        (
            ImageChops.multiply(blue, alpha),
            ImageChops.multiply(green, alpha),
            ImageChops.multiply(red, alpha),
            alpha,
        ),
    ).tobytes()


class Glow:
    """The light itself.

    The window is created once and then only shown and hidden, because building
    the bitmap costs about a fifth of a second and Vigil clicks in bursts - a
    fresh window per click would flicker and feel slow.
    """

    _class_registered = False
    _class_name = "VigilGlowOverlay"
    _proc = None

    def __init__(self, width: int, height: int):
        self.width = width
        self.height = height
        self.showing = False
        self.error = ""
        self.ready = threading.Event()
        self._thread = None
        self._hwnd = None
        self._bits = None
        self._closing = threading.Event()
        self._starting = threading.Lock()

    # ------------------------------------------------------------------ api
    def prepare(self, timeout: float = 8.0) -> bool:
        """Build the window, hidden, ready to be shown instantly. Idempotent."""
        if not IS_WINDOWS:
            return False
        with self._starting:
            if self._thread is None or not self._thread.is_alive():
                self._closing.clear()
                self.ready.clear()
                self._thread = threading.Thread(target=self._run, daemon=True)
                self._thread.start()
        self.ready.wait(timeout)
        return self._hwnd is not None

    def show(self) -> bool:
        if not self.prepare():
            return False
        try:
            user32 = ctypes.windll.user32
            handle = wintypes.HWND(self._hwnd)
            user32.ShowWindow(handle, SW_SHOWNOACTIVATE)
            # WS_EX_TOPMOST at creation is not always enough - ask for the top of
            # the z-order outright, without taking focus from whatever is there.
            user32.SetWindowPos(
                handle, wintypes.HWND(HWND_TOPMOST), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW,
            )
            self.showing = True
        except Exception as exc:
            self.error = str(exc)
            return False
        return True

    def hide(self) -> None:
        self.showing = False
        if self._hwnd:
            try:
                ctypes.windll.user32.ShowWindow(wintypes.HWND(self._hwnd), SW_HIDE)
            except Exception:
                pass

    def close(self) -> None:
        """Take the window down for good."""
        self.hide()
        self._closing.set()
        if self._hwnd:
            try:
                ctypes.windll.user32.PostMessageW(wintypes.HWND(self._hwnd), WM_QUIT, 0, 0)
            except Exception:
                pass

    # --------------------------------------------------------------- window
    def _register_class(self) -> bool:
        if Glow._class_registered:
            return True
        try:
            def proc(hwnd, message, wparam, lparam):
                return ctypes.windll.user32.DefWindowProcW(hwnd, message, wparam, lparam)

            # the callback must outlive the class registration, so it is kept here
            Glow._proc = WNDPROC(proc)
            cls = _WndClassEx()
            cls.cbSize = ctypes.sizeof(_WndClassEx)
            cls.style = 0
            cls.lpfnWndProc = Glow._proc
            cls.hInstance = ctypes.windll.kernel32.GetModuleHandleW(None)
            cls.lpszClassName = Glow._class_name
            if not ctypes.windll.user32.RegisterClassExW(ctypes.byref(cls)):
                return False
            Glow._class_registered = True
            return True
        except Exception as exc:
            self.error = "class registration raised: " + str(exc)
            return False

    def _run(self) -> None:
        """Owns the window. A Win32 window belongs to the thread that made it."""
        try:
            if not self._register_class():
                self.error = "could not register the window class: " + str(
                    ctypes.windll.kernel32.GetLastError()
                )
                self.ready.set()
                return

            user32 = ctypes.windll.user32
            gdi32 = ctypes.windll.gdi32

            # no WS_VISIBLE: painted up front, shown only when it is wanted
            hwnd = user32.CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
                Glow._class_name, "Vigil control", WS_POPUP,
                0, 0, self.width, self.height,
                None, None, ctypes.windll.kernel32.GetModuleHandleW(None), None,
            )
            if not hwnd:
                self.error = "could not create the window: " + str(
                    ctypes.windll.kernel32.GetLastError()
                )
                self.ready.set()
                return
            self._hwnd = hwnd

            self._paint(hwnd, gdi32, user32, build_glow(self.width, self.height))
            self.ready.set()

            message = wintypes.MSG()
            while not self._closing.is_set():
                if user32.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
                    if message.message == WM_QUIT:
                        break
                    user32.TranslateMessage(ctypes.byref(message))
                    user32.DispatchMessageW(ctypes.byref(message))
                else:
                    # nothing is routed here - the window takes no input at all -
                    # so this only has to be often enough to notice WM_QUIT
                    ctypes.windll.kernel32.Sleep(100)
        except Exception as exc:
            self.error = str(exc)
            self.ready.set()
        finally:
            self.showing = False
            if self._hwnd:
                try:
                    ctypes.windll.user32.DestroyWindow(wintypes.HWND(self._hwnd))
                except Exception:
                    pass
                self._hwnd = None

    def _paint(self, hwnd, gdi32, user32, image) -> None:
        width, height = self.width, self.height

        header = _BitmapInfoHeader()
        header.biSize = ctypes.sizeof(_BitmapInfoHeader)
        header.biWidth = width
        header.biHeight = -height          # negative: top-down, matching PIL
        header.biPlanes = 1
        header.biBitCount = 32
        header.biCompression = BI_RGB

        info = _BitmapInfo()
        info.bmiHeader = header

        screen_dc = user32.GetDC(None)
        memory_dc = gdi32.CreateCompatibleDC(screen_dc)

        bits = ctypes.c_void_p()
        bitmap = gdi32.CreateDIBSection(
            memory_dc, ctypes.byref(info), DIB_RGB_COLORS, ctypes.byref(bits), None, 0
        )
        old = gdi32.SelectObject(memory_dc, bitmap)

        buffer = _premultiplied(image)
        ctypes.memmove(bits, buffer, len(buffer))
        self._bits = buffer  # keep it alive

        blend = _BlendFunction(AC_SRC_OVER, 0, 255, AC_SRC_ALPHA)
        size = wintypes.SIZE(width, height)
        source = wintypes.POINT(0, 0)
        position = wintypes.POINT(0, 0)

        user32.UpdateLayeredWindow(
            wintypes.HWND(hwnd), screen_dc, ctypes.byref(position), ctypes.byref(size),
            memory_dc, ctypes.byref(source), 0, ctypes.byref(blend), ULW_ALPHA,
        )

        gdi32.SelectObject(memory_dc, old)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(memory_dc)
        user32.ReleaseDC(None, screen_dc)


class ControlLight:
    """Lights the screen while Vigil is working the mouse and keyboard.

    Every action that drives input pushes the deadline out, so a run of clicks
    reads as one continuous session rather than a strobe. Capture tools do not
    light it, and `suspend()` puts it out while a screenshot is taken, so the
    glow never ends up in the picture the model looks at.
    """

    #: tools that mean Vigil, not the person, is holding the controls
    TOOLS = frozenset({
        "mouse_click", "mouse_move", "mouse_scroll", "click_on",
        "keyboard_type", "press_keys", "focus_window",
    })

    LINGER = 1.1   # seconds the light stays up after the last action

    def __init__(self):
        self._glow = None
        self._until = 0.0
        self._suspended = 0
        self._lock = threading.Lock()
        self._watcher = None
        self._stop = threading.Event()

    # ------------------------------------------------------------------ api
    def prepare(self) -> None:
        """Build the overlay ahead of time, off the hot path."""
        if not IS_WINDOWS:
            return
        threading.Thread(target=self._ensure, daemon=True).start()

    def touch(self) -> None:
        """Vigil just reached for the mouse or the keyboard."""
        if not IS_WINDOWS or self._stop.is_set():
            return
        with self._lock:
            self._until = time.time() + self.LINGER
            suspended = self._suspended
        glow = self._ensure()
        if glow is not None and not suspended and not glow.showing:
            glow.show()
        self._watch()

    @contextmanager
    def suspend(self):
        """Put the light out for the duration - used while grabbing the screen."""
        with self._lock:
            self._suspended += 1
        glow = self._glow
        was_showing = glow is not None and glow.showing
        if was_showing:
            glow.hide()
            time.sleep(0.05)   # let the compositor take it away before the grab
        try:
            yield
        finally:
            with self._lock:
                self._suspended = max(0, self._suspended - 1)
                wanted = self._suspended == 0 and time.time() < self._until
            if wanted and glow is not None:
                glow.show()

    def stop(self) -> None:
        self._stop.set()
        with self._lock:
            self._until = 0.0
        if self._glow is not None:
            self._glow.close()
            self._glow = None

    # --------------------------------------------------------------- inside
    def _ensure(self):
        """One overlay, however many threads ask for it at once.

        The window is published before it is built, so a call arriving during
        the build waits on that same window instead of starting a second one.
        """
        with self._lock:
            if self._glow is None:
                from . import native

                width, height = native.screen_size()
                self._glow = Glow(width, height)
            glow = self._glow
        glow.prepare()
        return glow

    def _watch(self) -> None:
        """One thread puts the light out once the burst of actions ends."""
        with self._lock:
            if self._watcher is not None and self._watcher.is_alive():
                return
            self._watcher = threading.Thread(target=self._countdown, daemon=True)
            self._watcher.start()

    def _countdown(self) -> None:
        while not self._stop.is_set():
            with self._lock:
                remaining = self._until - time.time()
            if remaining <= 0:
                if self._glow is not None:
                    self._glow.hide()
                return
            time.sleep(min(0.15, remaining))


_LIGHT = None


def light() -> ControlLight:
    """The one control light, shared by the agent and the GUI tools."""
    global _LIGHT
    if _LIGHT is None:
        _LIGHT = ControlLight()
    return _LIGHT
