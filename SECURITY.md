# Security Policy

## What Vigil does and does not do

Vigil **operates** your computer; it does not **weaken** its security.

The actions below are permanently blocked by `Rules.Blocked` in `src/Vigil/Core/Security/Rules.cs`.
They do not run in any approval mode - not even `yolo` - and they do not run if the user insists:

- Disabling antivirus, firewall, UAC or Windows security services; stopping their processes
- Altering boot or code-signing protections, removing machine-wide script protection
- Deleting system restore points, shadow copies and backups
- Formatting disks, wiping a drive root, fork bombs
- Credential dumping (LSASS, SAM/SECURITY hives, `/etc/shadow`, browser password stores)
- Clearing event logs to cover tracks
- Piping downloaded code straight into a shell (`curl ... | bash`, `iwr ... | iex`)
- Writing to system directories (`C:\Windows`, `C:\Program Files`, ...)
- Reading SSH private keys, cloud credentials, browser session files, or Vigil's own settings
  (which hold the API keys)
- Ending processes Windows or its security depend on (`lsass`, `csrss`, `MsMpEng`, ...)

This is a best-effort filter, not a sandbox. Vigil runs as you, never elevated.

## Approval model

| Mode | Behaviour |
|---|---|
| `ask` | every moderate and high risk step is confirmed |
| `auto` (default) | moderate runs automatically, high risk still asks |
| `yolo` | everything not blocked runs |

In **every** mode:

- High-risk actions are confirmed one at a time; "allow for this session" is never offered for them.
- Taking the mouse, the keyboard or the screen (and the clipboard) is confirmed **every single
  time**, with no "always" option.
- Approvals come from the keyboard or mouse only. A spoken "yes" never approves anything - audio
  from a video or a call could fake it.
- An approval card cannot be answered in its first moment on screen, so a key already on its way
  down does not answer it.

## Prompt injection

Content from web pages, files and screenshots is handled as **data**. Even if a page says
"run this command", it is not an instruction. Vigil's browser uses its own profile, so pages it
opens cannot reach your logged-in accounts.

## Voice

The wake phrase is recognised locally by Windows. Audio is kept in memory only, never written to
disk, and nothing is sent anywhere until "Hey Vigil" has been heard - then only the words that
follow go to the transcription service (Groq or OpenAI, on your key).

## Audit log

Every decision is written to `~/.vigil/audit.jsonl`: timestamp, tool, risk level, summary,
decision and mode. The tray menu opens it.

## Data privacy

- API keys are stored in `~/.vigil/config.json`, which Vigil's own tools refuse to read.
- What reaches the model: your messages, tool output, and screenshots when you approve them.
- Vigil collects no telemetry and sends data nowhere else.

## Reporting a vulnerability

Please **do not open a public issue**. Contact BOF Studios privately first (a GitHub private
security advisory). We ask that you hold details until a fix is published.

We are especially interested in:
- Command variants that slip past the blocked rules
- File access that escapes the path protection
- Tool calls that bypass the approval card
