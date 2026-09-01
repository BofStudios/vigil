"""Keep the test suite out of the real ~/.vigil.

Several tests build an `Api` with a throwaway config, and some of its methods
save settings. Without this, running the tests overwrote the user's own
config.json - API key included. VIGIL_HOME is read when vigil.config is first
imported, so it has to be set here, before any test module imports vigil.
"""

import os
import tempfile
from pathlib import Path

_SANDBOX = Path(tempfile.mkdtemp(prefix="vigil-tests-"))
os.environ["VIGIL_HOME"] = str(_SANDBOX)

# A stray key in the environment would otherwise leak into every Config.load()
# and make the tests depend on whoever is running them.
for name in ("GROQ_API_KEY", "VIGIL_MODEL", "VIGIL_VISION_MODEL",
             "VIGIL_APPROVAL_MODE", "VIGIL_PROVIDER", "OLLAMA_HOST"):
    os.environ.pop(name, None)

import pytest  # noqa: E402

from vigil import config as vigil_config  # noqa: E402

SANDBOX = _SANDBOX


@pytest.fixture(autouse=True)
def _clean_config_file():
    """Every test starts with no config file, and leaves none behind."""
    if vigil_config.CONFIG_FILE.exists():
        vigil_config.CONFIG_FILE.unlink()
    yield
    if vigil_config.CONFIG_FILE.exists():
        vigil_config.CONFIG_FILE.unlink()
