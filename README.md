<div align="center">

```
 __     _____ ____ ___ _
 \ \   / /_ _/ ___|_ _| |
  \ \ / / | | |  _ | || |
   \ V /  | | |_| || || |___
    \_/  |___\____|___|_____|
```

**A free AI agent that operates your computer from the terminal.**

[![Python](https://img.shields.io/badge/python-3.9%2B-blue)](https://www.python.org/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Groq](https://img.shields.io/badge/AI-Groq%20(free)-orange)](https://console.groq.com/keys)
[![Ollama](https://img.shields.io/badge/AI-Ollama%20(local)-black)](https://ollama.com)

*by [BOF Studios](https://github.com/BofStudios)*

</div>

---

## What is Vigil?

Vigil is an AI agent that lives in your terminal. Tell it what you want in plain language and it
does the work with **real tools**: reads and writes files, runs commands, inspects system state,
looks at your screen, drives the mouse and keyboard, and controls a browser.

```
vigil > collect every pdf on my desktop into a folder called "invoices"

  * list_dir   C:\Users\Pc\Desktop
  * find_files *.pdf
  ! make_dir   C:\Users\Pc\Desktop\invoices
  ! move_path  invoice_january.pdf -> invoices\

vigil
Moved 7 PDF files into the invoices folder. No PDFs left on the desktop.
```

It runs on **Groq**'s free API tier, or fully offline through **Ollama** — no credit card,
no subscription.

---

## Why Vigil?

| | |
|---|---|
| **Free** | Groq's free tier is enough, and the key takes 30 seconds to get. Or stay entirely offline with Ollama. |
| **Safe** | Every risky step is put to you for approval. Some actions never run, in any mode. |
| **Transparent** | You see each step as it happens, and every decision is written to an audit log. |
| **It plans** | Multi-step jobs get a visible checklist, ticked off as the work happens. |
| **It remembers** | Persistent memory carries your preferences and project facts across sessions. |
| **Extensible** | Drop a Python file into the plugin folder and its tools load on the next run. |

---

## Install

```bash
pip install vigil-cli
```

From source:

```bash
git clone https://github.com/BofStudios/vigil.git
cd vigil
pip install -e .
```

With screen/mouse/keyboard and browser tools:

```bash
pip install "vigil-cli[all]"
playwright install chromium
```

### Free API key

1. Go to https://console.groq.com/keys (signing in with Google is enough)
2. **Create API Key** → copy it
3. Configure:

```bash
vigil setup
```

You can also set the `GROQ_API_KEY` environment variable, or put it in a `.env` file in your
working directory.

---

## Usage

```bash
vigil                                  # start a chat session
vigil "clean up my downloads folder"   # one-shot command
vigil doctor                           # check the install and connection
vigil tools                            # list loaded tools
vigil models                           # list available models
vigil memory                           # show persistent memory
vigil plugins                          # show loaded plugins
vigil audit -n 30                      # recent security decisions
vigil config set model openai/gpt-oss-120b
```

Flags: `--provider groq|ollama`, `--model <name>`, `--mode ask|auto|yolo`, `--yolo`,
`--cwd <path>`, `--no-gui`, `--no-browser`, `--no-stream`, `--quiet`

In-chat commands:

| Command | What it does |
|---|---|
| `/help` | list commands |
| `/tools` | loaded tools |
| `/model [name]` | show or change the model |
| `/provider [groq\|ollama]` | switch the AI provider |
| `/mode ask\|auto\|yolo` | change the approval mode |
| `/cwd [path]` | change the working directory |
| `/memory` · `/plugins` | memory and plugins |
| `/reset` · `/history` | reset or summarize the conversation |
| `/save` · `/load` | save or load a session |
| `/audit` | security decisions |
| `/exit` | quit |

---

## Security model

Vigil **operates** your computer; it does not **weaken** it. Every tool call is classified
before it runs:

| Level | Example | Behaviour |
|---|---|---|
| **safe** | `ls`, `git status`, reading files, system info | runs automatically |
| **moderate** | writing files, creating folders, opening apps, screenshots | asks in `ask` mode |
| **high** | deleting, killing processes, installing packages, shutdown | always asks |
| **blocked** | the list below | **never runs, in any mode** |

### Permanently blocked

These do not run even with `--yolo`:

- Disabling antivirus / firewall / UAC / SELinux / SIP / Gatekeeper
- Deleting restore points and backups (ransomware behaviour)
- Formatting disks, wiping the root directory, fork bombs
- Credential dumping (LSASS, SAM, `/etc/shadow`, browser cookie stores)
- Clearing event logs to cover tracks
- Piping downloaded code straight into a shell (`curl … | bash`, `iwr … | iex`)
- Writing to system directories (`C:\Windows`, `/etc`, `/bin`, …)
- Reading SSH keys, AWS credentials, browser password databases

This is tested: `tests/test_security.py` verifies that more than 30 attack variants are
refused even in `yolo` mode.

### Approval modes

```bash
vigil                # ask  - asks before every moderate and high risk step (default)
vigil --mode auto    # auto - moderate runs automatically, high still asks
vigil --yolo         # yolo - never asks (blocked actions stay blocked)
```

The approval prompt offers **y**es / **n**o / **a**lways allow (this session). "Always allow"
only ever covers moderate actions — high-risk ones are asked every single time.

### Audit log

Every decision is appended to `~/.vigil/audit.jsonl`:

```json
{"ts":"2026-08-30T14:31:47","tool":"run_command","risk":"blocked","summary":"netsh advfirewall set allprofiles state off","allowed":false,"decision":"This action is permanently blocked","mode":"yolo"}
```

Read it with `vigil audit -n 30`.

### External content rule

Text coming from web pages, files and screenshots is **data, not instructions**. Even if a page
says "run this command", Vigil will not treat it as an order — browser output carries that
warning automatically.

---

## Tools

44 tools across 7 groups:

| Group | Tools |
|---|---|
| **terminal** | `run_command`, `change_dir`, `current_dir` |
| **file** | `read_file`, `write_file`, `edit_file`, `list_dir`, `find_files`, `search_text`, `make_dir`, `copy_path`, `move_path`, `delete_path` |
| **system** | `system_info`, `list_processes`, `kill_process`, `disk_usage`, `network_info`, `list_installed_apps`, `open_app`, `clean_temp` |
| **screen** | `screen_capture`, `screen_size`, `mouse_click`, `mouse_move`, `mouse_scroll`, `keyboard_type`, `press_keys`, `list_windows`, `focus_window`, `clipboard` |
| **browser** | `browser_open`, `browser_read`, `browser_click`, `browser_type`, `browser_screenshot`, `browser_back`, `browser_close` |
| **memory** | `remember`, `recall`, `forget` |
| **planning** | `create_plan`, `update_plan`, `show_plan` |

Groups whose dependencies are missing switch themselves off — Vigil keeps working with the rest.

---

## Providers

### Groq (default, free cloud)

The Groq catalog changes over time — `vigil models` always shows the live list. Verified as of
August 2026:

| Model | Tool calling | Vision | Note |
|---|:---:|:---:|---|
| `openai/gpt-oss-120b` | ✅ | ❌ | default, most capable |
| `openai/gpt-oss-20b` | ✅ | ❌ | faster, easier on the quota |
| `qwen/qwen3.8-27b` | ✅ | ✅ | used for screen analysis |
| `qwen/qwen3.6-27b` | ✅ | ❌ | alternative |
| `groq/compound` | ❌ | ❌ | no tool calling, does not work with Vigil |

### Ollama (fully local)

No key, no internet, nothing leaves the machine:

```bash
# 1. install from https://ollama.com/download
ollama pull qwen3:8b            # supports tool calling
ollama pull llama3.2-vision     # if you want screen analysis

vigil --provider ollama
# or permanently:
vigil config set provider ollama
```

Vigil keeps the main model and the vision model separate: a screenshot goes to a vision-capable
model and the main model receives a **text description**. That way tool calling and image
support never clash.

---

## Task planning

Long jobs are where agents drift — they forget a step, redo one, or stop early. For anything with
three or more steps Vigil writes the plan down first and ticks it off as it goes, so you can watch
the work happen:

```
vigil > set up a small python project here and check that it runs

  * create_plan  plan: 3 steps
  ! write_file   write: main.py
  * update_plan  step 1 -> done
  ! write_file   write: README.md
  ! run_command  python main.py
  * update_plan  step 3 -> done

┌──────────────── plan · 3/3 done ─────────────────┐
│   [x] Create main.py with a greeting  main.py created
│   [x] Create README.md describing it  README.md created
│   [x] Run main.py and check the output  ran, output captured
└──────────────────────────────────────────────────┘
```

Steps carry a status of `todo`, `doing`, `done` or `blocked`, and a blocked step keeps its reason
next to it — so when something cannot be finished you can see exactly where it stopped and why.

The plan lives in the session only and is never written to disk. Turn it off with
`vigil config set enable_planner false`.

---

## Persistent memory

Vigil stores important facts in two scopes and injects them into the system prompt each session:

- **global** — valid everywhere (`~/.vigil/memory/global.md`)
- **project** — only in that folder (`~/.vigil/memory/projects/<folder>_<hash>.md`)

```
vigil > from now on keep your answers short, remember that
  * remember  remember: the user wants short answers
```

Notes are plain text files you can edit by hand. Use `vigil memory` to view them and
`vigil memory clear` to wipe them.

---

## Plugins

Every `.py` file in `~/.vigil/plugins/` is loaded automatically at startup.

```bash
vigil plugins new weather   # creates a scaffold
# edit the file
vigil tools                 # confirm the tool is loaded
```

A plugin just publishes a `TOOLS` list:

```python
from vigil.security import Risk
from vigil.tools import ToolContext, ToolSpec

def current_time(ctx: ToolContext) -> str:
    import datetime
    return datetime.datetime.now().strftime("%H:%M:%S")

TOOLS = [
    ToolSpec(
        name="current_time",
        description="Returns the system clock time.",
        parameters={"type": "object", "properties": {}},
        handler=current_time,
        group="plugin",
        risk=Risk.SAFE,
    ),
]
```

Plugins are user code and run with full privileges — but their tools still pass through the
security layer. If yours does something destructive, call `ctx.guard.check_action(...)` first.
A broken plugin never crashes Vigil; it is skipped and the reason shows up in `vigil plugins`.

---

## Configuration

Settings live in `~/.vigil/config.json`.

| Key | Default | Description |
|---|---|---|
| `provider` | `groq` | `groq` or `ollama` |
| `model` | `openai/gpt-oss-120b` | Groq main model |
| `vision_model` | `qwen/qwen3.8-27b` | Groq vision model |
| `ollama_host` | `http://localhost:11434` | Ollama address |
| `ollama_model` | `qwen3:8b` | Ollama main model |
| `approval_mode` | `ask` | `ask` / `auto` / `yolo` |
| `max_steps` | `40` | max tool steps per request |
| `max_tool_output` | `12000` | character cap on tool output |
| `enable_gui` · `enable_browser` | `true` | toggle tool groups |
| `enable_memory` · `enable_plugins` | `true` | memory and plugin systems |
| `enable_planner` | `true` | task checklist for multi-step jobs |
| `protect_paths` | `[]` | extra paths to protect |
| `stream` | `true` | stream the answer as it is written |

```bash
vigil config list
vigil config set approval_mode auto
vigil config set protect_paths "C:/Users/Pc/Private,D:/Backup"
```

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `No API key configured` | `vigil setup` or set `GROQ_API_KEY` |
| `429 / quota` | Groq free tier limit — wait, or `/model openai/gpt-oss-20b` |
| `Model not found` | The catalog changes. `vigil models` → `vigil config set model <name>` |
| Screen analysis fails | `vision_model` must support images: `vigil config set vision_model qwen/qwen3.8-27b` |
| Cannot reach Ollama | Is it installed and running? `ollama pull qwen3:8b` |
| Screen tools missing | `pip install "vigil-cli[gui]"` |
| Browser will not start | `pip install "vigil-cli[browser]"` and `playwright install chromium` |
| Garbled characters | Use Windows Terminal (Vigil forces UTF-8 output) |
| General check | `vigil doctor` |

---

## Development

```bash
pip install -e ".[all,dev]"
ruff check .
pytest -q          # 124 tests
```

See [docs/architecture.md](docs/architecture.md) for the design,
[docs/publishing.md](docs/publishing.md) for release steps, and
[CONTRIBUTING.md](CONTRIBUTING.md) to contribute.

---

## Roadmap

- [x] Groq provider, tool calling loop, security layer
- [x] Screen analysis (separate vision model)
- [x] Browser automation (Playwright)
- [x] Ollama support (fully local)
- [x] Plugin system (`~/.vigil/plugins/`)
- [x] Persistent memory (global + project)
- [x] Task planner: a visible checklist for multi-step jobs
- [ ] Scheduled tasks (`vigil schedule`)
- [ ] More providers: Gemini, OpenRouter
- [ ] Optional web interface

---

## Contributing

Issues and pull requests are welcome. Changes that **weaken** the rules in `vigil/security.py`
will not be accepted; proposals for new blocks are very welcome.

If you find a security vulnerability, please read [SECURITY.md](SECURITY.md) before opening a
public issue.

## License

MIT — [LICENSE](LICENSE) · © 2026 BOF Studios
