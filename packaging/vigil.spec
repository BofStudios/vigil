# PyInstaller spec for the Vigil desktop application.
#
# Builds a folder you can install: Vigil.exe plus its runtime, with Python
# inside it. Someone who buys this should never learn what pip is.
#
#     python tools/build_app.py
#
# Deliberately excluded: Playwright and its browser. It is a few hundred
# megabytes for a tool group that the registry already skips politely when the
# dependency is missing, and anyone who wants browser automation can install it
# alongside. Everything else the app actually uses is bundled.

import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files, collect_submodules

ROOT = Path(SPECPATH).resolve().parent
PACKAGE = ROOT / "vigil"

datas = [
    (str(PACKAGE / "desktop" / "web"), "vigil/desktop/web"),
    (str(PACKAGE / "assets"), "vigil/assets"),
]
# pywebview ships its own JS bridge as package data
datas += collect_data_files("webview")

hiddenimports = [
    # loaded by name at runtime, so PyInstaller cannot see them in the source
    "vigil.tools.files",
    "vigil.tools.shell",
    "vigil.tools.system",
    "vigil.tools.gui",
    "vigil.tools.planner",
    "vigil.tools.memory_tools",
    "vigil.tools.browser",
    "vigil.providers.anthropic_provider",
    "vigil.providers.groq_provider",
    "vigil.providers.ollama_provider",
    "vigil.providers.openai_provider",
]
hiddenimports += collect_submodules("webview")
hiddenimports += collect_submodules("pystray")

block_cipher = None

analysis = Analysis(
    [str(ROOT / "packaging" / "launch.py")],
    pathex=[str(ROOT)],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "playwright",       # hundreds of megabytes for an optional tool group
        "pytest",
        "ruff",
        "tkinter.test",
        "test",
        "unittest",
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(analysis.pure, analysis.zipped_data, cipher=block_cipher)

executable = EXE(
    pyz,
    analysis.scripts,
    [],
    exclude_binaries=True,
    name="Vigil",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,          # a double-clicked app has no console to print to
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=str(PACKAGE / "assets" / "vigil.ico"),
)

COLLECT(
    executable,
    analysis.binaries,
    analysis.zipfiles,
    analysis.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="Vigil",
)
