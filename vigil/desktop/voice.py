"""Push to talk: hold a key, say something, let go.

Deliberately hold-to-talk rather than always-listening. A microphone that is
only open while you are holding a key down is one you can reason about; one that
listens for a wake word is not, and this app can already move your mouse.

Nothing is kept: the audio lives in memory for the length of one request and is
never written to disk.
"""

from __future__ import annotations

import io
import threading
import wave

# Whisper resamples anything you send it, so recording at its own rate keeps the
# upload small and skips a conversion.
SAMPLE_RATE = 16000
CHANNELS = 1
SAMPLE_WIDTH = 2  # int16

MIN_SECONDS = 0.35   # shorter than this is a stray tap, not speech
MAX_SECONDS = 60.0   # a runaway hold should not upload forever


class VoiceUnavailable(RuntimeError):
    """Recording is not possible here - no library, or no microphone."""


def available() -> tuple:
    """(ok, reason) - whether this machine can record at all."""
    try:
        import sounddevice
    except Exception as exc:
        return False, 'sounddevice is not installed (pip install "vigil-cli[voice]") - ' + str(exc)
    try:
        if not any(d["max_input_channels"] > 0 for d in sounddevice.query_devices()):
            return False, "no microphone found"
    except Exception as exc:
        return False, "could not query audio devices: " + str(exc)
    return True, ""


class Recorder:
    """Records to memory while a key is held."""

    def __init__(self):
        self._stream = None
        self._frames: list = []
        self._lock = threading.Lock()
        self.recording = False

    def start(self) -> None:
        try:
            import sounddevice
        except Exception as exc:
            raise VoiceUnavailable("sounddevice is not installed") from exc

        with self._lock:
            if self.recording:
                return
            self._frames = []
            self.recording = True

        def callback(indata, _frames, _time, status):  # noqa: ANN001 - sounddevice signature
            if not self.recording:
                return
            with self._lock:
                # cap the buffer so a stuck key cannot eat memory
                if len(self._frames) * 1024 < SAMPLE_RATE * MAX_SECONDS:
                    self._frames.append(bytes(indata))

        try:
            self._stream = sounddevice.RawInputStream(
                samplerate=SAMPLE_RATE,
                channels=CHANNELS,
                dtype="int16",
                blocksize=1024,
                callback=callback,
            )
            self._stream.start()
        except Exception as exc:
            self.recording = False
            raise VoiceUnavailable("could not open the microphone: " + str(exc)) from exc

    def stop(self) -> bytes:
        """Stop and return a WAV, or b"" when there was nothing worth sending."""
        with self._lock:
            if not self.recording:
                return b""
            self.recording = False
            frames = self._frames
            self._frames = []

        if self._stream is not None:
            try:
                self._stream.stop()
                self._stream.close()
            except Exception:
                pass
            self._stream = None

        audio = b"".join(frames)
        seconds = len(audio) / float(SAMPLE_RATE * SAMPLE_WIDTH * CHANNELS)
        if seconds < MIN_SECONDS:
            return b""

        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as handle:
            handle.setnchannels(CHANNELS)
            handle.setsampwidth(SAMPLE_WIDTH)
            handle.setframerate(SAMPLE_RATE)
            handle.writeframes(audio)
        return buffer.getvalue()

    def cancel(self) -> None:
        self.stop()


class PushToTalk:
    """Watches for one key being held down, anywhere on the system.

    RegisterHotKey only reports the press, so this uses pynput, which reports
    both edges and works the same on Windows, macOS and Linux.
    """

    #  name -> pynput key
    KEYS = {
        "right ctrl": "ctrl_r",
        "left ctrl": "ctrl_l",
        "right alt": "alt_r",
        "right shift": "shift_r",
        "f8": "f8",
        "f9": "f9",
        "f10": "f10",
        "scroll lock": "scroll_lock",
        "pause": "pause",
    }

    def __init__(self, key_name: str, on_press=None, on_release=None):
        self.key_name = (key_name or "right ctrl").lower()
        self.on_press = on_press or (lambda: None)
        self.on_release = on_release or (lambda: None)
        self.listening = False
        self._listener = None
        self._down = False

    def start(self) -> tuple:
        """(ok, reason)"""
        attribute = self.KEYS.get(self.key_name)
        if attribute is None:
            return False, "unknown key: " + self.key_name

        try:
            from pynput import keyboard
        except Exception as exc:
            return False, 'pynput is not installed (pip install "vigil-cli[voice]") - ' + str(exc)

        target = getattr(keyboard.Key, attribute, None)
        if target is None:
            return False, "this platform has no " + self.key_name

        def pressed(key):
            if key == target and not self._down:
                self._down = True
                try:
                    self.on_press()
                except Exception:
                    pass

        def released(key):
            if key == target and self._down:
                self._down = False
                try:
                    self.on_release()
                except Exception:
                    pass

        try:
            self._listener = keyboard.Listener(on_press=pressed, on_release=released)
            self._listener.daemon = True
            self._listener.start()
            self.listening = True
        except Exception as exc:
            return False, "could not watch the keyboard: " + str(exc)
        return True, ""

    def stop(self) -> None:
        if self._listener is not None:
            try:
                self._listener.stop()
            except Exception:
                pass
            self._listener = None
        self.listening = False
