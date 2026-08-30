"""Configuration: ~/.vigil/config.json plus environment variables."""

from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

VIGIL_HOME = Path(os.environ.get("VIGIL_HOME", str(Path.home() / ".vigil")))
CONFIG_FILE = VIGIL_HOME / "config.json"
SESSION_DIR = VIGIL_HOME / "sessions"
AUDIT_LOG = VIGIL_HOME / "audit.jsonl"
HISTORY_FILE = VIGIL_HOME / "history"
SCREENSHOT_DIR = VIGIL_HOME / "screenshots"
PLUGIN_DIR = VIGIL_HOME / "plugins"
MEMORY_DIR = VIGIL_HOME / "memory"

DEFAULT_MODEL = "openai/gpt-oss-120b"
DEFAULT_VISION_MODEL = "qwen/qwen3.8-27b"

APPROVAL_MODES = ("ask", "auto", "yolo")
PROVIDERS = ("groq", "ollama")

DEFAULT_OLLAMA_HOST = "http://localhost:11434"
DEFAULT_OLLAMA_MODEL = "qwen3:8b"
DEFAULT_OLLAMA_VISION_MODEL = "llama3.2-vision"


@dataclass
class Config:
    """User settings. Every field can be changed with `vigil config set <key> <value>`."""

    provider: str = "groq"  # groq | ollama
    api_key: str = ""
    model: str = DEFAULT_MODEL
    vision_model: str = DEFAULT_VISION_MODEL
    approval_mode: str = "ask"  # ask | auto | yolo
    temperature: float = 0.3
    max_steps: int = 40
    max_tool_output: int = 12000
    max_history_messages: int = 60
    enable_gui: bool = True
    enable_browser: bool = True
    enable_system: bool = True
    enable_memory: bool = True
    enable_planner: bool = True
    enable_plugins: bool = True
    ollama_host: str = DEFAULT_OLLAMA_HOST
    ollama_model: str = DEFAULT_OLLAMA_MODEL
    ollama_vision_model: str = DEFAULT_OLLAMA_VISION_MODEL
    protect_paths: list[str] = field(default_factory=list)
    stream: bool = True

    # ---------- load / save ----------

    @classmethod
    def load(cls) -> Config:
        data: dict[str, Any] = {}
        if CONFIG_FILE.exists():
            try:
                data = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                data = {}
        known = set(cls.__dataclass_fields__)
        config = cls(**{k: v for k, v in data.items() if k in known})
        config._apply_env()
        return config

    def _apply_env(self) -> None:
        _load_dotenv()
        if os.environ.get("GROQ_API_KEY"):
            self.api_key = os.environ["GROQ_API_KEY"]
        if os.environ.get("VIGIL_MODEL"):
            self.model = os.environ["VIGIL_MODEL"]
        if os.environ.get("VIGIL_VISION_MODEL"):
            self.vision_model = os.environ["VIGIL_VISION_MODEL"]
        if os.environ.get("VIGIL_APPROVAL_MODE") in APPROVAL_MODES:
            self.approval_mode = os.environ["VIGIL_APPROVAL_MODE"]
        if os.environ.get("VIGIL_PROVIDER") in PROVIDERS:
            self.provider = os.environ["VIGIL_PROVIDER"]
        if os.environ.get("OLLAMA_HOST"):
            self.ollama_host = os.environ["OLLAMA_HOST"]

    def save(self) -> Path:
        VIGIL_HOME.mkdir(parents=True, exist_ok=True)
        CONFIG_FILE.write_text(
            json.dumps(asdict(self), indent=2, ensure_ascii=False), encoding="utf-8"
        )
        try:  # make the file user-readable only (POSIX)
            CONFIG_FILE.chmod(0o600)
        except OSError:
            pass
        return CONFIG_FILE

    # ---------- helpers ----------

    def set_value(self, key: str, raw: str) -> Any:
        if key not in self.__dataclass_fields__:
            raise KeyError(key)
        current = getattr(self, key)
        if isinstance(current, bool):
            value: Any = str(raw).strip().lower() in ("1", "true", "yes", "y", "on")
        elif isinstance(current, int) and not isinstance(current, bool):
            value = int(raw)
        elif isinstance(current, float):
            value = float(raw)
        elif isinstance(current, list):
            value = [part.strip() for part in raw.split(",") if part.strip()]
        else:
            value = raw
        if key == "approval_mode" and value not in APPROVAL_MODES:
            raise ValueError("approval_mode must be one of: " + ", ".join(APPROVAL_MODES))
        if key == "provider" and value not in PROVIDERS:
            raise ValueError("provider must be one of: " + ", ".join(PROVIDERS))
        setattr(self, key, value)
        return value

    # ---------- models for the active provider ----------

    @property
    def active_model(self) -> str:
        """Main model of the selected provider."""
        return self.ollama_model if self.provider == "ollama" else self.model

    @property
    def active_vision_model(self) -> str:
        """Vision model of the selected provider."""
        return self.ollama_vision_model if self.provider == "ollama" else self.vision_model

    def set_active_model(self, name: str) -> None:
        if self.provider == "ollama":
            self.ollama_model = name
        else:
            self.model = name

    def as_dict(self) -> dict[str, Any]:
        return asdict(self)

    def masked(self) -> dict[str, Any]:
        data = self.as_dict()
        key = data.get("api_key")
        if key:
            data["api_key"] = (key[:7] + "..." + key[-4:]) if len(key) > 14 else "***"
        return data


def _load_dotenv() -> None:
    """Load a .env file from the working directory. Existing variables win."""
    env_file = Path.cwd() / ".env"
    if not env_file.exists():
        return
    try:
        for line in env_file.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            key = key.strip()
            value = value.strip().strip('"').strip("'")
            if key and key not in os.environ:
                os.environ[key] = value
    except OSError:
        pass


def ensure_dirs() -> None:
    for path in (VIGIL_HOME, SESSION_DIR, SCREENSHOT_DIR, PLUGIN_DIR, MEMORY_DIR):
        path.mkdir(parents=True, exist_ok=True)
