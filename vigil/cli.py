"""Vigil command line interface."""

from __future__ import annotations

import argparse
import json
import platform
import sys
from pathlib import Path

from . import __version__, brains
from . import memory as memory_store
from .agent import Agent
from .config import (
    APPROVAL_MODES,
    AUDIT_LOG,
    CONFIG_FILE,
    HISTORY_FILE,
    PLUGIN_DIR,
    PROVIDERS,
    SESSION_DIR,
    VIGIL_HOME,
    Config,
    ensure_dirs,
)
from .providers import KNOWN_MODELS, AuthError, ProviderError, build_provider, provider_notes
from .security import Guard
from .templates import PLUGIN_TEMPLATE
from .tools import build_registry
from .ui import UI

SUBCOMMANDS = {"setup", "config", "models", "tools", "doctor", "audit", "chat",
               "memory", "plugins", "app", "version", "help"}

SLASH_HELP = [
    ("/help", "show this help"),
    ("/tools", "list loaded tools by group"),
    ("/model [name]", "show or change the model"),
    ("/provider [groq|ollama]", "switch the AI provider"),
    ("/models", "list the models available to you"),
    ("/mode [ask|auto|yolo]", "show or change the approval mode"),
    ("/brain [direct|autonomous]", "show or change how it thinks"),
    ("/cwd [path]", "show or change the working directory"),
    ("/clear", "clear the screen"),
    ("/reset", "reset the conversation history"),
    ("/history", "conversation summary"),
    ("/save [name]", "save the session"),
    ("/load <file>", "load a saved session"),
    ("/audit [n]", "show recent security decisions"),
    ("/memory", "show persistent memory notes"),
    ("/plugins", "show loaded plugins"),
    ("/exit", "quit"),
]


# ---------------------------------------------------------------- helpers
def _build(config: Config, ui: UI, cwd=None) -> Agent:
    guard = Guard(mode=config.approval_mode, confirm=ui.confirm, extra_protected=config.protect_paths)
    registry = build_registry(config)
    provider = build_provider(config)
    return Agent(config, provider, registry, guard, ui, cwd=cwd)


def _require_key(config: Config, ui: UI) -> bool:
    if config.provider == "ollama":
        return True  # the local provider needs no key
    if config.api_key:
        return True
    ui.error("No API key configured.")
    ui.info("1. Get a free key at https://console.groq.com/keys")
    ui.info("2. Run `vigil setup` (or set the GROQ_API_KEY environment variable)")
    return False


# ------------------------------------------------------------------ setup
def cmd_setup(args, config: Config, ui: UI) -> int:
    ensure_dirs()
    ui.console.print()
    ui.info("Vigil setup")
    ui.console.print()

    def ask(question: str, default: str = "") -> str:
        try:
            return input(question).strip()
        except (EOFError, KeyboardInterrupt):
            ui.console.print()
            return default

    # 1) provider
    ui.dim("groq   - free cloud API (needs a key, very fast)")
    ui.dim("ollama - fully local (no key, the model runs on your machine)")
    choice = ask("Provider groq/ollama [" + config.provider + "]: ").lower()
    if choice in PROVIDERS:
        config.provider = choice

    # 2) provider-specific settings
    if config.provider == "groq":
        ui.dim("Free key: https://console.groq.com/keys")
        if config.api_key:
            ui.dim("current key: " + config.api_key[:7] + "..." + config.api_key[-4:])
        entered = ask("GROQ API key (leave empty to keep the current one): ")
        if entered:
            config.api_key = entered
        if not config.api_key:
            ui.error("No key provided.")
            return 1
    else:
        ui.dim("Install Ollama: https://ollama.com/download")
        host = ask("Ollama host [" + config.ollama_host + "]: ")
        if host:
            config.ollama_host = host

    model_choice = ask("Model [" + config.active_model + "]: ")
    if model_choice:
        config.set_active_model(model_choice)

    mode_choice = ask("Approval mode ask/auto/yolo [" + config.approval_mode + "]: ").lower()
    if mode_choice in APPROVAL_MODES:
        config.approval_mode = mode_choice

    # 3) connection test
    ui.dim("testing the connection...")
    try:
        provider = build_provider(config)
        models = provider.list_models()
    except ProviderError as exc:
        ui.error(str(exc))
        return 1

    if models and config.active_model not in models:
        ui.warn(config.active_model + " is not in the list. Examples: " + ", ".join(models[:6]))
    note = provider_notes(config)
    if note:
        ui.warn(note)

    path = config.save()
    ui.success("Connected. " + str(len(models)) + " model(s) reachable.")
    ui.success("Settings saved to " + str(path))
    ui.dim("Start with: vigil")
    return 0


# ----------------------------------------------------------------- config
def cmd_config(args, config: Config, ui: UI) -> int:
    action = (args.action or "list").lower()

    if action == "list":
        rows = [(key, json.dumps(value, ensure_ascii=False)) for key, value in config.masked().items()]
        ui.table("vigil settings (" + str(CONFIG_FILE) + ")", ["key", "value"], rows)
        return 0

    if action == "path":
        ui.console.print(str(CONFIG_FILE))
        return 0

    if action == "get":
        if not args.key:
            ui.error("usage: vigil config get <key>")
            return 1
        value = config.masked().get(args.key)
        if value is None and args.key not in config.as_dict():
            ui.error("unknown key: " + args.key)
            return 1
        ui.console.print(json.dumps(value, ensure_ascii=False))
        return 0

    if action == "set":
        if not args.key or args.value is None:
            ui.error("usage: vigil config set <key> <value>")
            return 1
        try:
            applied = config.set_value(args.key, args.value)
        except KeyError:
            ui.error("unknown key: " + args.key)
            return 1
        except (ValueError, TypeError) as exc:
            ui.error(str(exc))
            return 1
        config.save()
        ui.success(args.key + " = " + json.dumps(applied, ensure_ascii=False))
        return 0

    ui.error("unknown subcommand: " + action)
    return 1


# ----------------------------------------------------------------- models
def cmd_models(args, config: Config, ui: UI) -> int:
    if not _require_key(config, ui):
        return 1
    try:
        provider = build_provider(config)
        models = provider.list_models()
    except ProviderError as exc:
        ui.error(str(exc))
        return 1

    rows = []
    for model in models:
        note = KNOWN_MODELS.get(model, "") if config.provider == "groq" else ""
        marks = []
        if model == config.active_model:
            marks.append("active")
        if model == config.active_vision_model:
            marks.append("vision")
        rows.append((model, note, ", ".join(marks)))

    ui.table(config.provider + " models", ["model", "description", "status"], rows)
    if not models:
        ui.dim("no models found. For Ollama: ollama pull " + config.active_model)
    key = "ollama_model" if config.provider == "ollama" else "model"
    ui.dim("to change: vigil config set " + key + " <name>")
    return 0


# ------------------------------------------------------------------ tools
def cmd_tools(args, config: Config, ui: UI) -> int:
    registry = build_registry(config)
    for group, specs in sorted(registry.groups().items()):
        rows = [(spec.name, spec.risk.label, spec.description.split(".")[0]) for spec in specs]
        ui.table(group + " (" + str(len(specs)) + ")", ["tool", "risk", "description"], rows)
    if registry.skipped:
        ui.console.print()
        ui.warn("tool groups that could not be loaded:")
        for module, reason in registry.skipped.items():
            ui.dim("  " + module.lstrip(".") + ": " + reason)
    return 0


# ----------------------------------------------------------------- doctor
def cmd_doctor(args, config: Config, ui: UI) -> int:
    rows = []

    rows.append(("python", "ok" if sys.version_info >= (3, 9) else "too old", platform.python_version()))
    rows.append(("operating system", "info", platform.system() + " " + platform.release()))
    rows.append(("config file", "found" if CONFIG_FILE.exists() else "missing", str(CONFIG_FILE)))
    rows.append(("provider", "ok", config.provider + " / model: " + config.active_model))
    if config.provider == "groq":
        rows.append(("api key", "set" if config.api_key else "missing", "GROQ_API_KEY / config"))
    else:
        rows.append(("ollama host", "info", config.ollama_host))

    for package, label in (("groq", "groq"), ("rich", "rich"), ("psutil", "psutil"),
                           ("pyautogui", "gui"), ("mss", "gui"), ("PIL", "gui"),
                           ("playwright", "browser")):
        try:
            __import__(package)
            rows.append((label + ": " + package, "ok", "installed"))
        except ImportError:
            hint = "pip install " + ("\"vigil-cli[gui]\"" if label == "gui" else
                                     "\"vigil-cli[browser]\"" if label == "browser" else package)
            rows.append((label + ": " + package, "missing", hint))

    if config.api_key or config.provider == "ollama":
        try:
            provider = build_provider(config)
            models = provider.list_models()
            rows.append((config.provider + " connection", "ok", str(len(models)) + " models"))
            rows.append(("model", "ok" if config.active_model in models else "check it", config.active_model))
            note = provider_notes(config)
            if note:
                rows.append(("model warning", "attention", note[:70]))
        except ProviderError as exc:
            rows.append((config.provider + " connection", "error", str(exc).splitlines()[0][:70]))

    registry = build_registry(config)
    rows.append(("tools", "ok", str(len(registry)) + " loaded"))
    for module, reason in registry.skipped.items():
        rows.append(("tool: " + module.lstrip("."), "skipped", reason[:70]))

    ui.table("vigil doctor", ["component", "status", "detail"], rows)
    return 0


# ------------------------------------------------------------------ audit
def cmd_audit(args, config: Config, ui: UI) -> int:
    if not AUDIT_LOG.exists():
        ui.dim("no audit records yet: " + str(AUDIT_LOG))
        return 0
    limit = int(getattr(args, "count", 20) or 20)
    lines = AUDIT_LOG.read_text(encoding="utf-8").splitlines()[-limit:]
    rows = []
    for line in lines:
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            continue
        rows.append(
            (
                record.get("ts", "")[-8:],
                record.get("tool", ""),
                record.get("risk", ""),
                "allowed" if record.get("allowed") else "denied",
                (record.get("summary") or "")[:60],
            )
        )
    ui.table("last " + str(len(rows)) + " decisions", ["time", "tool", "risk", "result", "action"], rows)
    ui.dim("full log: " + str(AUDIT_LOG))
    return 0


# ----------------------------------------------------------------- memory
def cmd_memory(args, config: Config, ui: UI) -> int:
    action = (getattr(args, "action", None) or "list").lower()
    cwd = Path(getattr(args, "cwd", None) or Path.cwd())

    if action == "clear":
        scope = getattr(args, "value", None) or "all"
        entries = memory_store.search("", scope=scope, cwd=cwd)
        if not entries:
            ui.dim("nothing to delete.")
            return 0
        ui.warn(str(len(entries)) + " note(s) will be deleted (scope: " + scope + ").")
        try:
            answer = input("are you sure? [y/N]: ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            answer = ""
        if answer not in ("y", "yes"):
            ui.dim("cancelled.")
            return 0
        for _name, entry in entries:
            memory_store.remove(entry.split("] ", 1)[-1][:40], scope=scope, cwd=cwd)
        ui.success("memory cleared.")
        return 0

    info = memory_store.stats(cwd)
    entries = memory_store.search("", scope="all", cwd=cwd)
    if entries:
        ui.table("memory (" + str(len(entries)) + " notes)", ["scope", "note"], entries)
    else:
        ui.dim("memory is empty. Vigil adds notes with the `remember` tool while it works.")
    ui.dim("global:  " + info["global_file"])
    ui.dim("project: " + info["project_file"])
    return 0


# ---------------------------------------------------------------- plugins
def cmd_plugins(args, config: Config, ui: UI) -> int:
    action = (getattr(args, "action", None) or "list").lower()
    PLUGIN_DIR.mkdir(parents=True, exist_ok=True)

    if action == "new":
        raw = (getattr(args, "value", None) or "").strip()
        name = "".join(ch if (ch.isalnum() or ch == "_") else "_" for ch in raw).lower()
        if not name or name[0].isdigit():
            ui.error("usage: vigil plugins new <name>   (must start with a letter)")
            return 1
        path = PLUGIN_DIR / (name + ".py")
        if path.exists():
            ui.error("already exists: " + str(path))
            return 1
        path.write_text(PLUGIN_TEMPLATE.format(name=name), encoding="utf-8")
        ui.success("plugin created: " + str(path))
        ui.dim("edit the file, then check it with `vigil tools`.")
        return 0

    registry = build_registry(config)
    rows = []
    for path in sorted(PLUGIN_DIR.glob("*.py")):
        if path.name.startswith("_"):
            rows.append((path.name, "skipped", "starts with an underscore"))
            continue
        problem = registry.skipped.get("plugin:" + path.name)
        rows.append((path.name, "error" if problem else "loaded", problem or "ok"))

    if rows:
        ui.table("plugins (" + str(PLUGIN_DIR) + ")", ["file", "status", "note"], rows)
    else:
        ui.dim("no plugins in " + str(PLUGIN_DIR))
        ui.dim("create one with: vigil plugins new <name>")

    plugin_tools = [spec for spec in registry.specs() if spec.group == "plugin"]
    if plugin_tools:
        ui.table("plugin tools", ["tool", "risk"], [(s.name, s.risk.label) for s in plugin_tools])
    return 0



# -------------------------------------------------------------------- app
def cmd_app(args, config: Config, ui: UI) -> int:
    """Open the desktop window."""
    wanted = getattr(args, "autostart", None)
    if wanted:
        from .desktop import shortcut

        if wanted == "status":
            where = shortcut.autostart_dir()
            if shortcut.autostart_enabled():
                ui.success("Vigil starts when you log in")
            else:
                ui.info("Vigil does not start on its own")
            if where:
                ui.dim("  " + str(where))
            return 0
        try:
            if wanted == "on":
                path = shortcut.enable_autostart()
                ui.success("Vigil will start when you log in")
                ui.dim("  " + str(path))
            else:
                removed = shortcut.disable_autostart()
                ui.success("Vigil will not start on its own"
                           + ("" if removed else " (it was not set to)"))
        except OSError as exc:
            ui.error(str(exc))
            return 1
        return 0

    if getattr(args, "install_shortcut", False):
        from .desktop import shortcut

        try:
            path = shortcut.create()
        except OSError as exc:
            ui.error(str(exc))
            return 1
        ui.success("Shortcut created: " + str(path))
        return 0

    # No key gate here on purpose. The app asks for one in its own window - a
    # double-clicked application has no terminal to print an error to.
    try:
        from .desktop.app import run
    except ImportError as exc:
        ui.error("The desktop app needs pywebview: pip install \"vigil-cli[desktop]\"")
        ui.dim(str(exc))
        return 1
    return run(config, debug=getattr(args, "debug", False))


# ------------------------------------------------------------------- chat
def cmd_chat(args, config: Config, ui: UI) -> int:
    if not _require_key(config, ui):
        return 1
    ensure_dirs()

    try:
        agent = _build(config, ui, cwd=args.cwd)
    except (AuthError, ProviderError) as exc:
        ui.error(str(exc))
        return 1

    prompt_text = " ".join(args.prompt or []).strip()

    # one-shot usage: vigil "do this"
    if prompt_text:
        try:
            agent.run(prompt_text)
        except KeyboardInterrupt:
            ui.console.print()
            ui.warn("stopped.")
        finally:
            agent.close()
        return 0

    return _repl(agent, config, ui)


def _repl(agent: Agent, config: Config, ui: UI) -> int:
    ui.banner(config.active_model, config.approval_mode, len(agent.registry),
              str(agent.ctx.cwd), brain=config.brain)
    note = provider_notes(config)
    if note:
        ui.warn(note)
    if agent.registry.skipped:
        for module, reason in agent.registry.skipped.items():
            ui.dim("  (" + module.lstrip(".") + " tools disabled: " + reason[:70] + ")")
        ui.console.print()

    session = _prompt_session()

    while True:
        try:
            user_input = session.prompt("vigil > ") if session is not None else input("vigil > ")
        except (EOFError, KeyboardInterrupt):
            ui.console.print()
            break

        user_input = (user_input or "").strip()
        if not user_input:
            continue

        if user_input.startswith("/"):
            if _slash(user_input, agent, config, ui) == "exit":
                break
            continue

        try:
            agent.run(user_input)
        except KeyboardInterrupt:
            ui.console.print()
            ui.warn("stopped.")
        except ProviderError as exc:
            ui.error(str(exc))

    agent.close()
    ui.dim("see you.")
    return 0


def _prompt_session():
    try:
        from prompt_toolkit import PromptSession
        from prompt_toolkit.completion import WordCompleter
        from prompt_toolkit.history import FileHistory

        VIGIL_HOME.mkdir(parents=True, exist_ok=True)
        completer = WordCompleter([item[0].split()[0] for item in SLASH_HELP], sentence=True)
        return PromptSession(history=FileHistory(str(HISTORY_FILE)), completer=completer)
    except Exception:
        return None


# ---------------------------------------------------------- slash commands
def _slash(line: str, agent: Agent, config: Config, ui: UI):
    parts = line.split()
    command = parts[0].lower()
    argument = " ".join(parts[1:]).strip()

    if command in ("/exit", "/quit", "/q"):
        return "exit"

    if command == "/help":
        ui.table("commands", ["command", "description"], SLASH_HELP)
        return None

    if command == "/tools":
        for group, specs in sorted(agent.registry.groups().items()):
            ui.table(group, ["tool", "risk"], [(s.name, s.risk.label) for s in specs])
        return None

    if command == "/models":
        try:
            models = agent.provider.list_models()
        except ProviderError as exc:
            ui.error(str(exc))
            return None
        ui.table("models", ["model", "description"], [(m, KNOWN_MODELS.get(m, "")) for m in models])
        return None

    if command == "/model":
        if not argument:
            ui.info(
                "provider: " + config.provider + "  |  model: " + config.active_model
                + "  |  vision model: " + config.active_vision_model
            )
            return None
        config.set_active_model(argument)
        agent.provider.model = argument
        config.save()
        ui.success("model changed to " + argument)
        note = provider_notes(config)
        if note:
            ui.warn(note)
        return None

    if command == "/provider":
        if not argument:
            ui.info("provider: " + config.provider + " (" + ", ".join(PROVIDERS) + ")")
            return None
        if argument not in PROVIDERS:
            ui.error("valid providers: " + ", ".join(PROVIDERS))
            return None
        config.provider = argument
        config.save()
        ui.success(
            "provider: " + argument + " (model: " + config.active_model + "). "
            "Restart Vigil for the change to take effect."
        )
        return None

    if command == "/mode":
        if not argument:
            ui.info("mode: " + config.approval_mode)
            return None
        if argument not in APPROVAL_MODES:
            ui.error("valid modes: " + ", ".join(APPROVAL_MODES))
            return None
        config.approval_mode = argument
        agent.guard.mode = argument
        config.save()
        agent.refresh_system_prompt()
        if argument == "yolo":
            ui.warn("yolo mode: moderate and high risk actions run without asking. "
                    "Blocked actions are still blocked.")
        ui.success("mode: " + argument)
        return None

    if command == "/brain":
        if not argument:
            chosen = brains.get(config.brain)
            ui.info("thinking: " + chosen.name.lower() + " - " + chosen.tagline.lower())
            ui.dim("  " + chosen.summary)
            return None
        if argument not in brains.names():
            ui.error("valid brains: " + ", ".join(brains.names()))
            return None

        chosen = brains.get(argument)
        config.brain = argument
        # picking a way of working picks a model too, the same as in the bar
        if config.provider == "groq":
            config.set_active_model(chosen.model)
            agent.provider.model = chosen.model
        config.save()
        agent.set_brain(argument)
        if chosen.warning:
            ui.warn(chosen.warning)
        ui.success("thinking: " + chosen.name.lower() + " (" + config.active_model + ")")
        return None

    if command == "/cwd":
        if not argument:
            ui.info(str(agent.ctx.cwd))
            return None
        target = Path(argument).expanduser()
        if not target.is_absolute():
            target = agent.ctx.cwd / target
        if not target.is_dir():
            ui.error("no such directory: " + str(target))
            return None
        agent.ctx.cwd = target.resolve()
        agent.refresh_system_prompt()
        ui.success("working directory: " + str(agent.ctx.cwd))
        return None

    if command == "/clear":
        ui.console.clear()
        return None

    if command == "/reset":
        agent.reset()
        ui.success("conversation history reset.")
        return None

    if command == "/history":
        counts = {}
        for message in agent.messages:
            counts[message.get("role", "?")] = counts.get(message.get("role", "?"), 0) + 1
        summary = ", ".join(role + ": " + str(count) for role, count in counts.items())
        ui.info(str(len(agent.messages)) + " messages (" + summary + ")")
        if getattr(agent.provider, "total_tokens", 0):
            ui.dim("total tokens: " + str(agent.provider.total_tokens)
                   + " | requests: " + str(agent.provider.request_count))
        return None

    if command == "/save":
        ui.success("saved to " + str(agent.save_session(argument)))
        return None

    if command == "/load":
        if not argument:
            files = sorted(SESSION_DIR.glob("*.json"))
            if not files:
                ui.dim("no saved sessions.")
                return None
            ui.table("sessions", ["file"], [(f.name,) for f in files[-15:]])
            return None
        candidate = Path(argument)
        if not candidate.exists():
            candidate = SESSION_DIR / argument
            if not candidate.suffix:
                candidate = candidate.with_suffix(".json")
        try:
            count = agent.load_session(candidate)
        except Exception as exc:
            ui.error("could not load it: " + str(exc))
            return None
        ui.success(str(count) + " messages loaded.")
        return None

    if command == "/memory":
        cmd_memory(argparse.Namespace(action="list", cwd=str(agent.ctx.cwd)), config, ui)
        return None

    if command == "/plugins":
        cmd_plugins(argparse.Namespace(action="list"), config, ui)
        return None

    if command == "/audit":
        cmd_audit(argparse.Namespace(count=int(argument) if argument.isdigit() else 20), config, ui)
        return None

    ui.error("unknown command: " + command + " (try /help)")
    return None


# ------------------------------------------------------------------- main
VALUE_FLAGS = {"--model", "--mode", "--brain", "--cwd", "--provider", "-n", "--count"}


def _first_positional_index(argv: list):
    """Index of the first real positional argument. Flag values are skipped."""
    skip_next = False
    for index, token in enumerate(argv):
        if skip_next:
            skip_next = False
            continue
        if token.startswith("-"):
            if token in VALUE_FLAGS:
                skip_next = True  # the next token is this flag's value
            continue
        return index
    return None


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="vigil",
        description="Vigil - a terminal-native AI agent that operates your computer (BOF Studios)",
        epilog="example:  vigil \"collect every pdf on my desktop into one folder\"",
    )
    parser.add_argument("--version", action="version", version="vigil " + __version__)
    parser.add_argument("--provider", choices=list(PROVIDERS), help="AI provider (groq or ollama)")
    parser.add_argument("--model", help="model to use for this run")
    parser.add_argument("--mode", choices=list(APPROVAL_MODES), help="approval mode")
    parser.add_argument(
        "--brain", choices=brains.names(),
        help="how it thinks: direct follows instructions, autonomous works out the route",
    )
    parser.add_argument("--yolo", action="store_true", help="never ask (blocked actions stay blocked)")
    parser.add_argument("--cwd", help="starting working directory")
    parser.add_argument("--no-stream", action="store_true", help="do not stream the answer")
    parser.add_argument("--no-gui", action="store_true", help="disable screen/mouse/keyboard tools")
    parser.add_argument("--no-browser", action="store_true", help="disable browser tools")
    parser.add_argument("--quiet", action="store_true", help="minimal output")

    subparsers = parser.add_subparsers(dest="command")

    subparsers.add_parser("setup", help="configure the API key and model")
    subparsers.add_parser("models", help="list available models")
    subparsers.add_parser("tools", help="list loaded tools")
    subparsers.add_parser("doctor", help="check the installation and connection")

    config_parser = subparsers.add_parser("config", help="show or change settings")
    config_parser.add_argument("action", nargs="?", default="list", choices=["list", "get", "set", "path"])
    config_parser.add_argument("key", nargs="?")
    config_parser.add_argument("value", nargs="?")

    audit_parser = subparsers.add_parser("audit", help="security decision log")
    audit_parser.add_argument("-n", "--count", type=int, default=20)

    memory_parser = subparsers.add_parser("memory", help="show or clear persistent memory")
    memory_parser.add_argument("action", nargs="?", default="list", choices=["list", "clear"])
    memory_parser.add_argument("value", nargs="?", default="all",
                               help="scope for clear: all, project, global")

    plugins_parser = subparsers.add_parser("plugins", help="list or create plugins")
    plugins_parser.add_argument("action", nargs="?", default="list", choices=["list", "new"])
    plugins_parser.add_argument("value", nargs="?", help="plugin name for `new`")

    app_parser = subparsers.add_parser("app", help="open the desktop app")
    app_parser.add_argument("--autostart", choices=["on", "off", "status"],
                            help="start Vigil when you log in")
    app_parser.add_argument("--install-shortcut", action="store_true",
                            help="put a Vigil shortcut on the desktop and exit")
    app_parser.add_argument("--debug", action="store_true", help="open the web inspector")

    chat_parser = subparsers.add_parser("chat", help="chat (default)")
    chat_parser.add_argument("prompt", nargs="*")

    return parser


def _force_utf8_output() -> None:
    """Keep non-ASCII characters intact on Windows consoles (cp850/cp1252 issue)."""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError, OSError):
            pass


def main(argv=None) -> int:
    _force_utf8_output()
    argv = list(sys.argv[1:] if argv is None else argv)

    # Anything that is not a subcommand is treated as a prompt: vigil "do this"
    index = _first_positional_index(argv)
    if index is not None and argv[index] not in SUBCOMMANDS:
        argv = argv[:index] + ["chat"] + argv[index:]

    args = build_parser().parse_args(argv)

    config = Config.load()
    if args.provider:
        config.provider = args.provider
    if args.model:
        config.set_active_model(args.model)
    if args.mode:
        config.approval_mode = args.mode
    if args.brain:
        config.brain = args.brain
        # the two ways of working run on different models, the same as in the bar
        if config.provider == "groq":
            config.set_active_model(brains.get(args.brain).model)
    if args.yolo:
        config.approval_mode = "yolo"
    if args.no_stream:
        config.stream = False
    if args.no_gui:
        config.enable_gui = False
    if args.no_browser:
        config.enable_browser = False

    ui = UI(quiet=args.quiet)

    command = args.command or "chat"
    if command == "chat" and not hasattr(args, "prompt"):
        args.prompt = []
    if not hasattr(args, "cwd"):
        args.cwd = None

    handlers = {
        "setup": cmd_setup,
        "config": cmd_config,
        "models": cmd_models,
        "tools": cmd_tools,
        "doctor": cmd_doctor,
        "audit": cmd_audit,
        "memory": cmd_memory,
        "plugins": cmd_plugins,
        "app": cmd_app,
        "chat": cmd_chat,
    }

    try:
        return handlers[command](args, config, ui) or 0
    except KeyboardInterrupt:
        ui.console.print()
        return 130
    except (AuthError, ProviderError) as exc:
        ui.error(str(exc))
        return 1


if __name__ == "__main__":
    sys.exit(main())
