"""GUI automation: screen analysis, mouse, keyboard, window management.

The screenshot is sent to a vision-capable model and the main model receives a text
description. That keeps tool calling and image support from clashing.
"""

from __future__ import annotations

import base64
import io
import platform
import time

from ..config import SCREENSHOT_DIR
from ..security import Risk
from . import PermissionDenied, ToolContext, ToolError, ToolSpec

MISSING_HINT = ""
try:
    import mss
    import pyautogui
    from PIL import Image

    pyautogui.FAILSAFE = True  # moving the mouse to the top-left corner aborts the action
    pyautogui.PAUSE = 0.15
    AVAILABLE = True
except Exception as exc:  # pragma: no cover - environment dependent
    mss = None
    pyautogui = None
    Image = None
    AVAILABLE = False
    MISSING_HINT = "GUI dependencies missing (pip install \"vigil-cli[gui]\") - " + str(exc)

IS_WINDOWS = platform.system() == "Windows"
MAX_IMAGE_WIDTH = 1400
JPEG_QUALITY = 70


def _capture(monitor: int = 1, region=None):
    """Grab the screen and return a PIL image."""
    with mss.mss() as sct:
        monitors = sct.monitors
        index = int(monitor)
        if index < 1 or index >= len(monitors):
            index = 1 if len(monitors) > 1 else 0
        area = monitors[index]
        if region:
            try:
                left, top, width, height = (int(v) for v in region)
                area = {"left": left, "top": top, "width": width, "height": height}
            except (TypeError, ValueError) as exc:
                raise ToolError("region must be [left, top, width, height].") from exc
        raw = sct.grab(area)
        return Image.frombytes("RGB", raw.size, raw.bgra, "raw", "BGRX"), area


def _encode(image) -> str:
    if image.width > MAX_IMAGE_WIDTH:
        ratio = MAX_IMAGE_WIDTH / float(image.width)
        image = image.resize((MAX_IMAGE_WIDTH, int(image.height * ratio)), Image.LANCZOS)
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=JPEG_QUALITY, optimize=True)
    return base64.b64encode(buffer.getvalue()).decode("ascii")


# ----------------------------------------------------------- screen_capture
def screen_capture(ctx: ToolContext, question: str = "", monitor: int = 1, region=None, save: bool = False) -> str:
    """Capture the screen, ask the vision model about it and return a text answer."""
    prompt = question.strip() or "What is on this screen? Describe open windows, key text and clickable elements."

    allowed, reason = ctx.guard.check_action(
        "screen_capture",
        "a screenshot will be taken and sent to the AI model: " + prompt[:120],
        Risk.MODERATE,
        "screen content leaves the machine",
        detail="Everything visible (open windows, messages) goes to the model.",
    )
    if not allowed:
        raise PermissionDenied(reason)

    image, area = _capture(monitor, region)
    encoded = _encode(image)

    saved_path = ""
    if save:
        SCREENSHOT_DIR.mkdir(parents=True, exist_ok=True)
        saved_path = str(SCREENSHOT_DIR / ("screen_" + time.strftime("%Y%m%d_%H%M%S") + ".jpg"))
        image.save(saved_path, format="JPEG", quality=85)

    if ctx.provider is None:
        raise ToolError("No vision model connection available.")

    detailed_prompt = (
        prompt
        + "\n\nScreen resolution: " + str(area.get("width")) + "x" + str(area.get("height"))
        + ". If something needs to be clicked, give its approximate pixel coordinates (x, y)."
    )
    try:
        description = ctx.provider.vision(detailed_prompt, encoded)
    except Exception as exc:
        raise ToolError("Image analysis failed: " + str(exc)) from exc

    result = "[screen analysis]\n" + description
    if saved_path:
        result += "\n\nSaved to: " + saved_path
    return result


# ------------------------------------------------------------- screen_size
def screen_size(ctx: ToolContext) -> str:
    width, height = pyautogui.size()
    position = pyautogui.position()
    try:
        with mss.mss() as sct:
            monitors = str(max(0, len(sct.monitors) - 1)) + " monitor(s)"
    except Exception:
        monitors = "1 monitor"
    return (
        "Main screen: " + str(width) + "x" + str(height)
        + " | Mouse at: (" + str(position.x) + ", " + str(position.y) + ") | " + monitors
    )


# -------------------------------------------------------------- mouse_click
def mouse_click(ctx: ToolContext, x: int, y: int, button: str = "left", clicks: int = 1) -> str:
    width, height = pyautogui.size()
    if not (0 <= int(x) < width and 0 <= int(y) < height):
        raise ToolError(
            "Coordinates are off screen: (" + str(x) + ", " + str(y) + "). Screen is "
            + str(width) + "x" + str(height)
        )
    if button not in ("left", "right", "middle"):
        raise ToolError("button must be left, right or middle.")

    summary = button + " click at (" + str(x) + ", " + str(y) + ")" + (" x" + str(clicks) if clicks > 1 else "")
    allowed, reason = ctx.guard.check_action("mouse_click", summary, Risk.MODERATE, "mouse control")
    if not allowed:
        raise PermissionDenied(reason)

    pyautogui.click(x=int(x), y=int(y), clicks=int(clicks), button=button)
    return summary + " done."


# --------------------------------------------------------------- mouse_move
def mouse_move(ctx: ToolContext, x: int, y: int, duration: float = 0.3) -> str:
    allowed, reason = ctx.guard.check_action(
        "mouse_move", "moving the mouse to (" + str(x) + ", " + str(y) + ")", Risk.SAFE, "mouse movement"
    )
    if not allowed:
        raise PermissionDenied(reason)
    pyautogui.moveTo(int(x), int(y), duration=float(duration))
    return "Mouse moved to (" + str(x) + ", " + str(y) + ")."


# ------------------------------------------------------------- mouse_scroll
def mouse_scroll(ctx: ToolContext, amount: int, x: int = -1, y: int = -1) -> str:
    allowed, reason = ctx.guard.check_action(
        "mouse_scroll", "scrolling by " + str(amount), Risk.SAFE, "page scroll"
    )
    if not allowed:
        raise PermissionDenied(reason)
    if x >= 0 and y >= 0:
        pyautogui.moveTo(int(x), int(y))
    pyautogui.scroll(int(amount))
    return "Scrolled by " + str(amount) + "."


# ------------------------------------------------------------ keyboard_type
def keyboard_type(ctx: ToolContext, text: str, interval: float = 0.02) -> str:
    if not text:
        raise ToolError("Nothing to type.")
    preview = text if len(text) <= 120 else text[:117] + "..."
    allowed, reason = ctx.guard.check_action(
        "keyboard_type",
        "typing: " + preview,
        Risk.MODERATE,
        "keyboard input",
        detail="The text goes to whichever window currently has focus.",
    )
    if not allowed:
        raise PermissionDenied(reason)
    pyautogui.write(text, interval=float(interval))
    return str(len(text)) + " character(s) typed."


# --------------------------------------------------------------- press_keys
def press_keys(ctx: ToolContext, keys: str) -> str:
    """Examples: enter, tab, ctrl+c, alt+f4, win+r"""
    combo = [part.strip().lower() for part in str(keys).replace(" ", "").split("+") if part.strip()]
    if not combo:
        raise ToolError("Key combination is empty.")

    allowed, reason = ctx.guard.check_action(
        "press_keys", "key: " + "+".join(combo), Risk.MODERATE, "keyboard shortcut"
    )
    if not allowed:
        raise PermissionDenied(reason)

    if len(combo) == 1:
        pyautogui.press(combo[0])
    else:
        pyautogui.hotkey(*combo)
    return "+".join(combo) + " pressed."


# ------------------------------------------------------------- list_windows
def list_windows(ctx: ToolContext) -> str:
    titles = []
    if IS_WINDOWS:
        try:
            import pygetwindow as gw

            for window in gw.getAllWindows():
                title = (window.title or "").strip()
                if title:
                    state = "active" if window.isActive else ("minimized" if window.isMinimized else "open")
                    titles.append(
                        title[:70] + "  [" + state + ", " + str(window.width) + "x" + str(window.height) + "]"
                    )
        except Exception as exc:
            raise ToolError("Could not list windows: " + str(exc)) from exc
    else:
        try:
            titles = [str(t) for t in pyautogui.getAllTitles() if str(t).strip()]
        except Exception as exc:
            raise ToolError("Window listing is not supported on this platform: " + str(exc)) from exc

    if not titles:
        return "No open windows found."
    return str(len(titles)) + " window(s):\n" + "\n".join("  " + t for t in titles[:40])


# ------------------------------------------------------------- focus_window
def focus_window(ctx: ToolContext, title: str) -> str:
    if not IS_WINDOWS:
        raise ToolError("Focusing windows is currently supported on Windows only.")
    try:
        import pygetwindow as gw

        matches = [w for w in gw.getAllWindows() if title.lower() in (w.title or "").lower()]
    except Exception as exc:
        raise ToolError("Could not search windows: " + str(exc)) from exc

    if not matches:
        raise ToolError("No window with that title: " + title)

    allowed, reason = ctx.guard.check_action(
        "focus_window", "bringing to front: " + (matches[0].title or "")[:80], Risk.MODERATE, "window focus"
    )
    if not allowed:
        raise PermissionDenied(reason)

    window = matches[0]
    try:
        if window.isMinimized:
            window.restore()
        window.activate()
    except Exception:
        try:
            window.minimize()
            window.restore()
        except Exception as exc:
            raise ToolError("Could not bring the window to front: " + str(exc)) from exc
    time.sleep(0.4)
    return (window.title or "") + " brought to front."


# ---------------------------------------------------------------- clipboard
def clipboard(ctx: ToolContext, action: str = "read", text: str = "") -> str:
    try:
        import pyperclip
    except ImportError as exc:
        raise ToolError("pyperclip is not installed.") from exc

    if action == "write":
        allowed, reason = ctx.guard.check_action(
            "clipboard", "writing to the clipboard: " + text[:80], Risk.MODERATE, "clipboard change"
        )
        if not allowed:
            raise PermissionDenied(reason)
        pyperclip.copy(text)
        return "Clipboard updated (" + str(len(text)) + " characters)."

    allowed, reason = ctx.guard.check_action(
        "clipboard", "reading the clipboard", Risk.MODERATE, "clipboard read (may contain secrets)"
    )
    if not allowed:
        raise PermissionDenied(reason)
    content = pyperclip.paste() or ""
    if len(content) > 4000:
        content = content[:4000] + "\n... (truncated)"
    return "Clipboard contents:\n" + content


TOOLS = [
    ToolSpec(
        name="screen_capture",
        description=(
            "Capture the screen and have a vision model analyse it. Use it to see what is on "
            "screen, locate an element or check the state of an application."
        ),
        parameters={
            "type": "object",
            "properties": {
                "question": {"type": "string", "description": "What to ask about the screen (empty = general description)"},
                "monitor": {"type": "integer", "description": "Monitor number (1 = primary)", "default": 1},
                "region": {
                    "type": "array",
                    "items": {"type": "integer"},
                    "description": "Optional region [left, top, width, height]",
                },
                "save": {"type": "boolean", "description": "Also save the image to disk", "default": False},
            },
        },
        handler=screen_capture,
        group="screen",
        risk=Risk.MODERATE,
        preview=lambda a: "screen analysis: " + str(a.get("question", ""))[:60],
    ),
    ToolSpec(
        name="screen_size",
        description="Return the screen resolution and the current mouse position.",
        parameters={"type": "object", "properties": {}},
        handler=screen_size,
        group="screen",
        risk=Risk.SAFE,
        preview=lambda a: "screen size",
    ),
    ToolSpec(
        name="mouse_click",
        description="Click at the given screen coordinates.",
        parameters={
            "type": "object",
            "properties": {
                "x": {"type": "integer"},
                "y": {"type": "integer"},
                "button": {"type": "string", "enum": ["left", "right", "middle"], "default": "left"},
                "clicks": {"type": "integer", "default": 1},
            },
            "required": ["x", "y"],
        },
        handler=mouse_click,
        group="screen",
        risk=Risk.MODERATE,
        preview=lambda a: "click (" + str(a.get("x")) + ", " + str(a.get("y")) + ")",
    ),
    ToolSpec(
        name="mouse_move",
        description="Move the mouse to the given coordinates.",
        parameters={
            "type": "object",
            "properties": {
                "x": {"type": "integer"},
                "y": {"type": "integer"},
                "duration": {"type": "number", "default": 0.3},
            },
            "required": ["x", "y"],
        },
        handler=mouse_move,
        group="screen",
        risk=Risk.SAFE,
        preview=lambda a: "mouse -> (" + str(a.get("x")) + ", " + str(a.get("y")) + ")",
    ),
    ToolSpec(
        name="mouse_scroll",
        description="Scroll up (positive) or down (negative).",
        parameters={
            "type": "object",
            "properties": {
                "amount": {"type": "integer", "description": "Positive scrolls up, negative scrolls down"},
                "x": {"type": "integer", "default": -1},
                "y": {"type": "integer", "default": -1},
            },
            "required": ["amount"],
        },
        handler=mouse_scroll,
        group="screen",
        risk=Risk.SAFE,
        preview=lambda a: "scroll " + str(a.get("amount")),
    ),
    ToolSpec(
        name="keyboard_type",
        description="Type text into the focused window. Never type passwords or secrets.",
        parameters={
            "type": "object",
            "properties": {
                "text": {"type": "string"},
                "interval": {"type": "number", "default": 0.02},
            },
            "required": ["text"],
        },
        handler=keyboard_type,
        group="screen",
        risk=Risk.MODERATE,
        preview=lambda a: "type: " + str(a.get("text", ""))[:60],
    ),
    ToolSpec(
        name="press_keys",
        description="Send a key or shortcut. Examples: enter, tab, ctrl+c, alt+f4, win+r",
        parameters={
            "type": "object",
            "properties": {"keys": {"type": "string"}},
            "required": ["keys"],
        },
        handler=press_keys,
        group="screen",
        risk=Risk.MODERATE,
        preview=lambda a: "key: " + str(a.get("keys", "")),
    ),
    ToolSpec(
        name="list_windows",
        description="List open windows and their state.",
        parameters={"type": "object", "properties": {}},
        handler=list_windows,
        group="screen",
        risk=Risk.SAFE,
        preview=lambda a: "list windows",
    ),
    ToolSpec(
        name="focus_window",
        description="Bring a window matching the title to the front (Windows only).",
        parameters={
            "type": "object",
            "properties": {"title": {"type": "string", "description": "Window title (partial match)"}},
            "required": ["title"],
        },
        handler=focus_window,
        group="screen",
        risk=Risk.MODERATE,
        preview=lambda a: "focus window: " + str(a.get("title", "")),
    ),
    ToolSpec(
        name="clipboard",
        description="Read from or write to the system clipboard.",
        parameters={
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["read", "write"], "default": "read"},
                "text": {"type": "string", "description": "Text to write when action is write"},
            },
        },
        handler=clipboard,
        group="screen",
        risk=Risk.MODERATE,
        preview=lambda a: "clipboard " + str(a.get("action", "read")),
    ),
]
