"""Push-to-talk tests.

Nothing here opens a microphone or reaches a network: the recorder is driven
through its own API with synthetic audio, and transcription goes to a stub.
"""

import io
import wave

import pytest

from vigil.config import Config
from vigil.desktop.app import Api
from vigil.desktop.voice import PushToTalk, Recorder


def _wav(seconds: float = 1.0) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(16000)
        handle.writeframes(b"\x00\x00" * int(16000 * seconds))
    return buffer.getvalue()


@pytest.fixture
def config():
    settings = Config()
    settings.api_key = "not-a-real-key"
    return settings


# ------------------------------------------------------------------ recorder
def test_stopping_a_recorder_that_never_started():
    assert Recorder().stop() == b""


def test_a_clip_shorter_than_a_tap_is_thrown_away():
    """A key brushed for a fraction of a second is not speech."""
    recorder = Recorder()
    recorder.recording = True
    recorder._frames = [b"\x00\x00" * 400]     # ~0.025s
    assert recorder.stop() == b""


def test_a_real_clip_comes_back_as_a_playable_wav():
    recorder = Recorder()
    recorder.recording = True
    recorder._frames = [b"\x00\x00" * 16000]   # 1 second
    audio = recorder.stop()

    with wave.open(io.BytesIO(audio)) as handle:
        assert handle.getnchannels() == 1
        assert handle.getframerate() == 16000
        assert handle.getsampwidth() == 2
        assert handle.getnframes() == 16000


def test_the_buffer_is_emptied_between_takes():
    recorder = Recorder()
    recorder.recording = True
    recorder._frames = [b"\x00\x00" * 16000]
    recorder.stop()

    recorder.recording = True
    recorder._frames = [b"\x00\x00" * 16000]
    second = recorder.stop()
    with wave.open(io.BytesIO(second)) as handle:
        assert handle.getnframes() == 16000   # not 32000


# ------------------------------------------------------------- the talk key
def test_an_unknown_key_is_refused():
    ok, reason = PushToTalk("banana").start()
    assert ok is False
    assert "unknown key" in reason


def test_the_key_names_map_to_real_modifiers():
    for name in ("right ctrl", "left ctrl", "right alt", "f9"):
        assert name in PushToTalk.KEYS


# ------------------------------------------------------------ the app wiring
class _StubProvider:
    def __init__(self, text="open the downloads folder"):
        self.text = text
        self.seen = []

    def transcribe(self, audio, filename="speech.wav", language="en"):
        self.seen.append((len(audio), language))
        return self.text


def _api_with_voice(config, provider):
    api = Api(config)
    tab = api.new_tab()
    api.sessions[tab["id"]].agent.provider = provider
    api.recorder = Recorder()
    return api


def test_transcribed_speech_is_offered_as_text(config):
    provider = _StubProvider()
    api = _api_with_voice(config, provider)

    events = []
    api.emit = events.append
    api._transcribe(_wav())

    assert events[-1]["type"] == "voice"
    assert events[-1]["state"] == "text"
    assert events[-1]["text"] == "open the downloads folder"
    # the language hint is passed through; Whisper is much better with it
    assert provider.seen[0][1] == "en"


def test_a_silent_clip_says_nothing(config):
    """Whisper answers silence with a lone full stop; that is not a message."""
    api = _api_with_voice(config, _StubProvider(text="."))
    events = []
    api.emit = events.append
    api._transcribe(_wav())

    assert events[-1]["state"] == "idle"


def test_a_transcription_failure_is_reported_not_swallowed(config):
    class Failing:
        def transcribe(self, *args, **kwargs):
            raise RuntimeError("service is down")

    api = _api_with_voice(config, Failing())
    events = []
    api.emit = events.append
    api._transcribe(_wav())

    assert events[-1]["state"] == "error"
    assert "service is down" in events[-1]["text"]


def test_releasing_without_holding_does_nothing(config):
    api = _api_with_voice(config, _StubProvider())
    events = []
    api.emit = events.append

    api.stop_listening()            # no press came first
    assert events == []


def test_state_reports_whether_the_talk_key_is_live(config):
    api = Api(config)
    api.new_tab()
    assert api.state()["voice"] is False
    assert api.state()["voice_key"] == config.voice_key
