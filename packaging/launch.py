"""What Vigil.exe runs.

A frozen application has no terminal, so anything that would normally be
printed has to go somewhere a person can find. Failures that stop the app
starting are written next to the config and shown in a message box, because a
window that never appears is the worst thing an application can do.
"""

from __future__ import annotations

import sys
import traceback


def _report(message: str) -> None:
    """Say what went wrong, in a way someone without a terminal will see."""
    try:
        from vigil.config import VIGIL_HOME

        VIGIL_HOME.mkdir(parents=True, exist_ok=True)
        (VIGIL_HOME / "startup-error.log").write_text(message, encoding="utf-8")
    except Exception:
        pass

    try:
        import ctypes

        ctypes.windll.user32.MessageBoxW(
            None,
            message[:1400] + "\n\nThis was also saved to startup-error.log "
            "in your .vigil folder.",
            "Vigil could not start",
            0x10,  # MB_ICONERROR
        )
    except Exception:
        print(message, file=sys.stderr)


def main() -> int:
    try:
        from vigil.desktop.app import run

        return run(debug="--debug" in sys.argv)
    except SystemExit:
        raise
    except BaseException:
        _report(traceback.format_exc())
        return 1


if __name__ == "__main__":
    sys.exit(main())
