"""Create a desktop shortcut for the Vigil app.

Windows gets a real .lnk (built through WScript.Shell, no extra dependency),
Linux gets a .desktop entry, macOS gets a small launcher script.
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
