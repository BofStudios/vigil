"""The suite must never touch the real ~/.vigil.

An earlier version of these tests called Api.set_mode(), which saves settings -
and so quietly overwrote the developer's own config.json, API key included.
This is the guard that keeps that from happening again.
"""

from vigil import config as vigil_config
from vigil.config import Config
from vigil.desktop.app import Api

from .conftest import SANDBOX


def test_config_home_is_a_sandbox():
    assert str(vigil_config.VIGIL_HOME) == str(SANDBOX)
    assert ".vigil" not in str(vigil_config.VIGIL_HOME)


def test_saving_writes_inside_the_sandbox():
    config = Config()
    config.api_key = "not-a-real-key"
    path = config.save()

    assert str(path).startswith(str(SANDBOX))
    assert path.exists()


def test_an_api_that_saves_cannot_escape_the_sandbox():
    """This is the exact path that clobbered the real config."""
    config = Config()
    config.api_key = "not-a-real-key"

    api = Api(config)
    api.new_tab()
    api.set_mode("auto")          # saves

    assert str(vigil_config.CONFIG_FILE).startswith(str(SANDBOX))
