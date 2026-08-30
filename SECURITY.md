# Security Policy

## What Vigil does and does not do

Vigil **operates** your computer; it does not **weaken** its security.

The actions below are permanently blocked by `BLOCKED_RULES` in `vigil/security.py`. They do not
run in `--yolo` mode, and they do not run even if the user insists:

- Disabling antivirus, firewall, UAC, SELinux, SIP or Gatekeeper
- Deleting system restore points and backups
- Formatting disks, wiping the root directory, fork bombs
- Credential dumping (LSASS, SAM/SECURITY hives, `/etc/shadow`, browser password stores)
- Clearing event logs to cover tracks
- Piping downloaded code straight into a shell (`curl ... | bash`, `iwr ... | iex`)
- Writing to system directories (`C:\Windows`, `/etc`, `/bin`, ...)
- Reading SSH private keys, AWS credentials, browser session files

This is a best-effort filter, not a sandbox. Do not run Vigil on machines you do not trust,
or with prompts you do not trust.

## Approval model

| Mode | Behaviour |
|---|---|
| `ask` (default) | every moderate and high risk step is confirmed |
| `auto` | moderate runs automatically, high risk still asks |
| `yolo` | nothing is asked, blocked actions stay blocked |

"Always allow" is never accepted for high-risk actions; those are confirmed one at a time.

## Prompt injection

Content from web pages, files and screenshots is handled as **data**. Even if a page says
"run this command", it is not an instruction. We still recommend staying in `ask` mode when
working with an agent that reads external content.

## Audit log

Every decision is written to `~/.vigil/audit.jsonl`: timestamp, tool, risk level, summary,
decision and mode. Inspect it with `vigil audit -n 50`.

## Data privacy

- The API key is stored in plain text in `~/.vigil/config.json` (mode `0600` on POSIX systems).
- What reaches the model: your messages, tool output, and screenshots if you use those tools.
- Screen capture tools always ask first; whatever is on screen goes to the model.
- Vigil collects no telemetry and sends data nowhere else.

## Reporting a vulnerability

Please **do not open a public issue**. Contact BOF Studios privately first (GitHub private
security advisory, or Instagram DM). We ask that you hold details until a fix is published.

We are especially interested in:
- Command variants that slip past `BLOCKED_RULES`
- File access that escapes the path protection
- Tool calls that bypass the approval prompt
