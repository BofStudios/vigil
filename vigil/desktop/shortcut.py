"""Shortcuts: one on the desktop, and one that starts Vigil with the machine.

Windows gets a real .lnk (built through WScript.Shell, no extra dependency),
Linux gets a .desktop entry, macOS gets a small launcher script.

Starting with the machine is a file in a folder the system already watches, not
a registry Run key: the user can see it, and delete it, without knowing what a
registry key is.
"""

from __future__ import annotations

import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path

ICON = Path(__file__).resolve().parent.parent / "assets" / "vigil.ico"
PNG = Path(__file__).resolve().parent.parent / "assets" / "vigil.png"


def desktop_dir() -> Path:
    """Best guess at the user's desktop, falling back to the home directory."""
    if platform.system() == "Windows":
        try:
            import winreg

            key = r"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as handle:
                value = winreg.QueryValueEx(handle, "Desktop")[0]
                path = Path(os.path.expandvars(value))
                if path.is_dir():
                    return path
        except (ImportError, OSError):
            pass
    for candidate in (Path.home() / "Desktop", Path.home() / "Masaüstü", Path.home()):
        if candidate.is_dir():
            return candidate
    return Path.home()


def _launcher() -> tuple:
    """(executable, arguments) that starts the app without a console window."""
    gui_exe = Path(sys.executable).with_name("vigil-app.exe")
    if gui_exe.exists():
        return str(gui_exe), ""

    scripts = Path(sys.executable).parent / "Scripts" / "vigil-app.exe"
    if scripts.exists():
        return str(scripts), ""

    pythonw = Path(sys.executable).with_name("pythonw.exe")
    if pythonw.exists():
        return str(pythonw), "-m vigil app"

    return sys.executable, "-m vigil app"


def create(name: str = "Vigil") -> Path:
    """Create the shortcut and return where it landed."""
    system = platform.system()
    target = desktop_dir()

    if system == "Windows":
        return _create_windows(target / (name + ".lnk"))
    if system == "Linux":
        return _create_linux(target / (name.lower() + ".desktop"))
    return _create_posix_script(target / name)


def _create_windows(path: Path) -> Path:
    executable, arguments = _launcher()
    script = (
        "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + str(path) + "');"
        "$s.TargetPath = '" + executable + "';"
        "$s.Arguments = '" + arguments + "';"
        "$s.WorkingDirectory = '" + str(Path.home()) + "';"
        "$s.IconLocation = '" + str(ICON) + "';"
        "$s.Description = 'Vigil - AI agent that operates your computer';"
        "$s.Save()"
    )
    powershell = shutil.which("powershell") or "powershell"
    result = subprocess.run(
        [powershell, "-NoProfile", "-NonInteractive", "-Command", script],
        capture_output=True,
        text=True,
        timeout=60,
    )
    if result.returncode != 0:
        raise OSError("Could not create the shortcut: " + (result.stderr or "").strip()[:200])
    return path


def _create_linux(path: Path) -> Path:
    executable, arguments = _launcher()
    entry = "\n".join(
        [
            "[Desktop Entry]",
            "Type=Application",
            "Name=Vigil",
            "Comment=AI agent that operates your computer",
            "Exec=" + executable + (" " + arguments if arguments else ""),
            "Icon=" + str(PNG),
            "Terminal=false",
            "Categories=Utility;Development;",
            "",
        ]
    )
    path.write_text(entry, encoding="utf-8")
    path.chmod(0o755)
    return path


def _create_posix_script(path: Path) -> Path:
    executable, arguments = _launcher()
    path.write_text(
        "#!/bin/sh\nexec " + executable + " " + arguments + " \"$@\"\n", encoding="utf-8"
    )
    path.chmod(0o755)
    return path


# ------------------------------------------------------------ start with windows
AUTOSTART_NAME = "Vigil"


def autostart_dir():
    """Where this system keeps things that run at login, or None if unknown."""
    system = platform.system()

    if system == "Windows":
        try:
            import winreg

            key = r"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as handle:
                value = winreg.QueryValueEx(handle, "Startup")[0]
                path = Path(os.path.expandvars(value))
                if path.is_dir():
                    return path
        except (ImportError, OSError):
            pass
        fallback = (Path(os.path.expandvars("%APPDATA%")) / "Microsoft" / "Windows"
                    / "Start Menu" / "Programs" / "Startup")
        return fallback if fallback.is_dir() else None

    if system == "Darwin":
        return Path.home() / "Library" / "LaunchAgents"

    return Path.home() / ".config" / "autostart"


def _autostart_path():
    folder = autostart_dir()
    if folder is None:
        return None
    system = platform.system()
    if system == "Windows":
        return folder / (AUTOSTART_NAME + ".lnk")
    if system == "Darwin":
        return folder / "com.bofstudios.vigil.plist"
    return folder / "vigil.desktop"


def autostart_enabled() -> bool:
    path = _autostart_path()
    return bool(path and path.exists())


def enable_autostart() -> Path:
    """Start Vigil when the user logs in. Returns where the entry was written."""
    path = _autostart_path()
    if path is None:
        raise OSError("Could not find this system's startup folder.")
    path.parent.mkdir(parents=True, exist_ok=True)

    system = platform.system()
    if system == "Windows":
        return _create_windows(path)
    if system == "Darwin":
        return _create_launch_agent(path)
    return _create_linux(path)


def disable_autostart() -> bool:
    """Stop starting with the machine. True if there was something to remove."""
    path = _autostart_path()
    if path is None or not path.exists():
        return False
    try:
        path.unlink()
    except OSError as exc:
        raise OSError("Could not remove the startup entry: " + str(exc)) from exc
    return True


def _create_launch_agent(path: Path) -> Path:
    """A macOS LaunchAgent. RunAtLoad only - nothing is kept alive or respawned."""
    executable, arguments = _launcher()
    program = [executable] + (arguments.split() if arguments else [])
    entries = "".join("      <string>" + part + "</string>\n" for part in program)
    path.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" '
        '"http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
        '<plist version="1.0">\n'
        "  <dict>\n"
        "    <key>Label</key>\n"
        "    <string>com.bofstudios.vigil</string>\n"
        "    <key>ProgramArguments</key>\n"
        "    <array>\n" + entries +
        "    </array>\n"
        "    <key>RunAtLoad</key>\n"
        "    <true/>\n"
        "  </dict>\n"
        "</plist>\n",
        encoding="utf-8",
    )
    return path
