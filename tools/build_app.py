"""Build Vigil.exe.

    python tools/build_app.py            # build the application folder
    python tools/build_app.py --zip      # and a portable zip beside it

The result is dist/Vigil/ with Vigil.exe in it and Python inside. It runs on a
machine that has never had Python installed, which is the whole point: someone
who buys this should never learn what pip is.

Windows only. The equivalent on macOS is a .app bundle and its own signing
story, which is a separate piece of work.
"""

from __future__ import annotations

import platform
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SPEC = ROOT / "packaging" / "vigil.spec"
DIST = ROOT / "dist"
BUILD = ROOT / "build"
APP = DIST / "Vigil"

# Everything the frozen app needs. Playwright is left out on purpose - see the
# note in the spec file.
NEEDED = [
    "pyinstaller>=6.0",
    "pywebview>=5.0",
    "pystray>=0.19",
    "pillow>=10.0.0",
    "mss>=9.0.1",
    "pyautogui>=0.9.54",
    "pyperclip>=1.8.2",
    "pygetwindow>=0.0.9",
    "pynput>=1.7",
    "sounddevice>=0.4.6",
]


def _version() -> str:
    text = (ROOT / "vigil" / "__init__.py").read_text(encoding="utf-8")
    for line in text.splitlines():
        if line.startswith("__version__"):
            return line.split("=", 1)[1].strip().strip('"').strip("'")
    return "0.0.0"


def _run(command: list) -> None:
    print("  $", " ".join(command[:4]), "…" if len(command) > 4 else "")
    result = subprocess.run(command, cwd=str(ROOT))
    if result.returncode != 0:
        raise SystemExit("failed: " + " ".join(command))


def install_requirements() -> None:
    print("· making sure the build dependencies are here")
    _run([sys.executable, "-m", "pip", "install", "--quiet", *NEEDED])


def build() -> Path:
    print("· clearing the last build")
    for folder in (BUILD, APP):
        if folder.exists():
            shutil.rmtree(folder, ignore_errors=True)

    print("· freezing")
    _run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", str(SPEC)])

    executable = APP / "Vigil.exe"
    if not executable.exists():
        raise SystemExit("PyInstaller finished but there is no Vigil.exe")
    return executable


def check(executable: Path) -> None:
    """The things that have actually gone missing before."""
    print("· checking what came out")
    carried = {
        "the front end": APP / "_internal" / "vigil" / "desktop" / "web" / "index.html",
        "the stylesheet": APP / "_internal" / "vigil" / "desktop" / "web" / "style.css",
        "the icon": APP / "_internal" / "vigil" / "assets" / "vigil.ico",
        "the tray image": APP / "_internal" / "vigil" / "assets" / "vigil.png",
    }
    missing = [name for name, path in carried.items() if not path.exists()]
    if missing:
        raise SystemExit("missing from the build: " + ", ".join(missing))

    size = sum(f.stat().st_size for f in APP.rglob("*") if f.is_file())
    print("  Vigil.exe   ", round(executable.stat().st_size / 1e6, 1), "MB")
    print("  the folder  ", round(size / 1e6, 1), "MB")


def zip_up() -> Path:
    archive = DIST / ("Vigil-" + _version() + "-windows-portable.zip")
    print("· zipping a portable copy")
    if archive.exists():
        archive.unlink()
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for path in sorted(APP.rglob("*")):
            if path.is_file():
                bundle.write(path, str(Path("Vigil") / path.relative_to(APP)))
    print("  ", archive.name, round(archive.stat().st_size / 1e6, 1), "MB")
    return archive


def main() -> int:
    if platform.system() != "Windows":
        print("This builds the Windows application. Run it on Windows.")
        return 1

    print("Vigil " + _version() + " - building the application\n")
    install_requirements()
    executable = build()
    check(executable)
    if "--zip" in sys.argv:
        zip_up()

    print("\nDone.  " + str(APP))
    print("Double-click Vigil.exe - no Python needed on the machine that runs it.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
