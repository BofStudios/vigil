# Architecture

Vigil is four layers deep. Each layer knows the one below it and nothing above it.

```
user
   |
   +-- desktop/ ...... the bar: pywebview window, tray, hot key, one session per tab
   |
  cli.py ............ arguments, REPL, slash commands
   |
  agent.py .......... model loop, conversation history, tool dispatch
   |         \
   |          ui.py ......... terminal output, approval dialogs
   |
  tools/ ............ tools (file, terminal, system, screen, browser, memory, planning)
   |         \
   |          memory.py ..... persistent notes (global + project)
   |
  security.py ....... risk classification, hard blocks, approval engine, audit log
   |
  providers/ ........ LLM connection (Groq, Ollama)
```

## Flow

1. `cli.py` loads the configuration, builds the `Guard`, `Registry` and `Provider`, then the `Agent`.
2. A user message goes through `Agent.run()`; tools are exposed as OpenAI-compatible schemas.
3. When the model calls a tool, `Agent._execute()` runs:
   - unknown parameters are dropped
   - the risk level is computed with `ToolSpec.risk_for(args)`
   - the tool calls `ctx.guard.check_*()` internally
   - the `Guard` decides: allow automatically / ask the user / refuse permanently
   - the decision is appended to `~/.vigil/audit.jsonl`
4. The result (or the error / denial message) is fed back to the model and the loop continues.
5. Once the model stops calling tools, the final answer is printed.

## Key design decisions

**Security lives in one place, not in each tool.** Tools do not implement their own checks;
they all go through the same `Guard`. Adding a tool cannot weaken the security model.

**Hard blocks are mode-independent.** The `Risk.BLOCKED` branch is the first thing
`Guard._decide()` evaluates; not even `yolo` mode gets past it.

**Vision goes to a separate model.** Tool-calling models often cannot process images, so
`screen_capture` sends the screenshot to a dedicated vision model and hands the main model
**text** back.

**External content is data.** Browser and file tool output carries a note stating that the
content is not an instruction, and the system prompt repeats the rule.

**The provider is swappable.** Any class implementing the `Provider` interface in
`providers/base.py` works. Groq is cloud, Ollama is fully local; `build_provider` picks based on
the `provider` config field. The Ollama provider uses only the standard library.

**Plugins are privileged but supervised.** `~/.vigil/plugins/*.py` is loaded at startup. Plugin
code has the same privileges as the user, but its tools still pass through the Guard, so the risk
policy still applies. A broken plugin never stops the program; it is recorded in `skipped`.

**The glass is the compositor's, not CSS.** `desktop/native.py` asks DWM for an acrylic
backdrop and rounded corners through ctypes. CSS cannot blur what is behind a window, so faking
it was never an option; where the API is missing the calls no-op and the CSS fallback stands in.

**The window size is set after it opens.** pywebview does not honour the height passed to
`create_window` for a frameless window - it comes out at `min_size` - but `resize()` sets the
viewport exactly, so `_polish_window` fits the bar once the window has settled.

**The desktop app reuses the agent, it does not fork it.** `desktop/session.py` provides a UI
adapter with the same surface the terminal UI has, turning every call into a JSON event for the
front end. Approvals block the worker thread on an Event until the window answers, which is exactly
what the terminal does with a blocking prompt.

**The plan is state, not prose.** `tools/planner.py` keeps the checklist in the session state and
draws it itself; those tools set `quiet_result` so the agent does not echo the same text twice. The
model is told to plan anything with three or more steps, which keeps long jobs from drifting.

**Memory is injected into the system prompt.** `memory.as_prompt()` merges global and
project-scoped notes into the system prompt, keeping the newest ones when the character cap is hit.

**Dependencies are optional.** `tools/gui.py` and `tools/browser.py` publish `AVAILABLE = False`
when their dependencies are missing; `Registry.load_module` skips them and records the reason in
`skipped`. The program keeps running.

## File map

| File | Responsibility |
|---|---|
| `vigil/cli.py` | command line, REPL, slash commands |
| `vigil/agent.py` | model loop, history management, session save/load |
| `vigil/ui.py` | rich-based output, approval screens |
| `vigil/security.py` | risk rules, `Guard`, audit log |
| `vigil/config.py` | `~/.vigil/config.json`, environment variables, `.env` |
| `vigil/memory.py` | persistent memory: global and project notes |
| `vigil/tools/planner.py` | task checklist kept in session state |
| `vigil/desktop/app.py` | the bar: window, geometry, and the JS-callable bridge |
| `vigil/desktop/native.py` | Windows acrylic, rounded corners, global hot key |
| `vigil/desktop/tray.py` | tray icon; closing hides rather than quits |
| `vigil/desktop/session.py` | per-tab agent plus the event-emitting UI adapter |
| `vigil/desktop/web/` | the front end: one HTML, one CSS, one JS file |
| `vigil/templates.py` | plugin scaffold template |
| `vigil/providers/base.py` | provider interface, message types |
| `vigil/providers/groq_provider.py` | Groq connection, streaming, vision, model list |
| `vigil/providers/ollama_provider.py` | local Ollama connection (standard library only) |
| `vigil/tools/__init__.py` | `ToolSpec`, `ToolContext`, `Registry`, plugin loading |
| `vigil/tools/*.py` | the tool groups |
