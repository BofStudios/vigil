"""Security layer: risk classification, hard blocks, approval engine and audit log.

Design principle: Vigil operates the computer, it does NOT weaken its security.
Nothing classified as BLOCKED ever runs, in any mode, including --yolo.
"""

from __future__ import annotations

import json
import re
import time
from dataclasses import dataclass
from enum import IntEnum
from pathlib import Path
from typing import Callable, Optional

from .config import AUDIT_LOG, VIGIL_HOME


class Risk(IntEnum):
    SAFE = 0       # read-only or trivially reversible -> runs automatically
    MODERATE = 1   # changes the system -> asks by default
    HIGH = 2       # hard to reverse / destructive -> always asks
    BLOCKED = 3    # never runs

    @property
    def label(self) -> str:
        return {0: "safe", 1: "moderate", 2: "high", 3: "blocked"}[int(self)]

    @property
    def color(self) -> str:
        return {0: "green", 1: "yellow", 2: "red", 3: "bright_red"}[int(self)]


# --------------------------------------------------------------------------
# 1) HARD BLOCKS - commands that disable security controls, steal credentials,
#    destroy recovery options or wipe forensic traces.
# --------------------------------------------------------------------------
BLOCKED_RULES = [
    # --- disabling security software ---
    (r"set-mppreference[^\n]*-disable\w*\s+\$?true", "disabling Windows Defender protection"),
    (r"add-mppreference[^\n]*-exclusionpath", "hiding a folder from Defender scans"),
    (r"(uninstall|remove)-windowsfeature[^\n]*defender", "removing Windows Defender"),
    (r"netsh\s+advfirewall\s+set\s+\w+\s+state\s+off", "turning off the Windows firewall"),
    (r"set-netfirewallprofile[^\n]*-enabled\s+false", "disabling a firewall profile"),
    (r"(systemctl|service)\s+(stop|disable)\s+(ufw|firewalld|apparmor)", "disabling the Linux firewall"),
    (r"setenforce\s+0", "disabling SELinux"),
    (r"enablelua[^\n]*/d\s*0", "disabling UAC (User Account Control)"),
    (r"consentpromptbehavioradmin[^\n]*/d\s*0", "disabling the UAC elevation prompt"),
    (r"set-executionpolicy[^\n]*(unrestricted|bypass)[^\n]*(machine|localmachine)",
     "removing machine-wide PowerShell script protection"),
    (r"bcdedit[^\n]*(nointegritychecks|testsigning|safeboot|recoveryenabled\s+no)",
     "altering Windows boot or code-signing protections"),
    (r"csrutil\s+disable", "disabling macOS System Integrity Protection"),
    (r"spctl\s+--master-disable", "disabling macOS Gatekeeper"),

    # --- destroying backups and recovery (ransomware behaviour) ---
    (r"vssadmin[^\n]*delete\s+shadows", "deleting system restore points"),
    (r"wbadmin[^\n]*delete\s+(catalog|backup|systemstatebackup)", "deleting Windows backups"),
    (r"wmic[^\n]*shadowcopy[^\n]*delete", "deleting shadow copies"),
    (r"reagentc[^\n]*/disable", "disabling the Windows recovery environment"),

    # --- disk and root destruction ---
    (r"\brm\s+(-[a-z]*\s+)*-[a-z]*[rf][a-z]*\s+(-[a-z]+\s+)*/(\s|$|\*)", "deleting the root directory"),
    (r"\bmkfs(\.\w+)?\b", "formatting a disk"),
    (r"\bformat\s+[a-z]:", "formatting a drive"),
    (r"\bdiskpart\b[^\n]*\bclean\b", "wiping a disk (diskpart clean)"),
    (r"\bdd\s+[^\n]*of=/dev/(sd|nvme|disk)", "raw writing to a disk device"),
    (r"del\s+/[a-z\s/]*\s+[a-z]:\\\*", "mass-deleting a drive root"),
    (r"remove-item[^\n]*\s[a-z]:\\?\s*(-recurse|$)", "mass-deleting a drive root"),
    (r":\(\)\s*\{\s*:\|:&\s*\}\s*;:", "fork bomb"),

    # --- credential theft ---
    (r"\bmimikatz\b|\bsekurlsa\b|\blsadump\b", "credential dumping tool"),
    (r"(procdump|rundll32)[^\n]*lsass", "LSASS memory dump"),
    (r"reg\s+save\s+hk(lm|ey_local_machine)\\(sam|security|system)", "copying the password database"),
    (r"\bsecretsdump\b|\bhashdump\b|\bcreddump\b", "credential dumping tool"),
    (r"(cat|type|copy|cp)\s+[^\n]*/etc/shadow", "reading or copying /etc/shadow"),

    # --- covering tracks ---
    (r"wevtutil\s+cl\b", "clearing Windows event logs"),
    (r"clear-eventlog\b", "clearing Windows event logs"),
    (r"(rm|shred|truncate)[^\n]*/var/log", "destroying system logs"),
    (r"\bcipher\s+/w", "making deleted data unrecoverable"),
    (r"history\s+-c\b", "clearing shell history"),

    # --- download-and-execute (unverified remote code) ---
    (r"(curl|wget)\s[^\n|]*\|\s*(sudo\s+)?(ba|z|s|fi)?sh", "piping downloaded code straight into a shell"),
    (r"(iwr|invoke-webrequest|invoke-restmethod|irm)[^\n]*\|\s*(iex|invoke-expression)",
     "piping downloaded code straight into a shell"),
    (r"iex\s*\(\s*new-object\s+net\.webclient", "piping downloaded code straight into a shell"),

    # --- system-wide privilege handover ---
    (r"icacls\s+[a-z]:\\?\s+/grant\s+\w+:\(?f\)?", "granting full control over a drive root"),
    (r"takeown\s+/f\s+[a-z]:\\?\s+/r", "taking ownership of a drive root"),
    (r"chmod\s+-r\s+777\s+/(\s|$)", "opening all permissions on the root directory"),
]

# --------------------------------------------------------------------------
# 2) HIGH RISK - allowed, but always requires explicit approval.
# --------------------------------------------------------------------------
HIGH_RULES = [
    (r"\brm\s+(-[a-z]*\s+)*-[a-z]*[rf]", "recursive or forced delete"),
    (r"remove-item[^\n]*-(recurse|force)", "recursive or forced delete"),
    (r"\b(rmdir|rd)\s+/s", "deleting a directory tree"),
    (r"del\s+/[sqf]", "bulk file delete"),
    (r"\b(shutdown|restart-computer|stop-computer|reboot|halt|poweroff)\b", "shutting down or rebooting"),
    (r"\btaskkill\b|\bkill\s+-9\b|\bpkill\b|stop-process", "terminating processes"),
    (r"\breg\s+(delete|add)\b|remove-itemproperty|set-itemproperty[^\n]*hk(lm|cu)",
     "registry modification"),
    (r"\bsc\s+(delete|stop|config)\b|stop-service|set-service", "Windows service modification"),
    (r"\bnet\s+(user|localgroup)\b[^\n]*(/add|/delete)", "user account modification"),
    (r"\bschtasks\b[^\n]*/(create|delete)|register-scheduledtask", "creating or deleting a scheduled task"),
    (r"\b(useradd|userdel|usermod|passwd)\b", "user account modification"),
    (r"\b(pip|pip3|npm|yarn|pnpm|winget|choco|brew|apt|apt-get|dnf|pacman)\s+(install|add|upgrade|remove|uninstall)",
     "installing or removing packages"),
    (r"\bgit\s+push\b[^\n]*(--force|-f)\b", "force push"),
    (r"\bgit\s+reset\s+--hard\b|\bgit\s+clean\s+-[a-z]*f", "permanently discarding local changes"),
    (r"\bsudo\b|\brunas\b|start-process[^\n]*-verb\s+runas", "running with administrator privileges"),
    (r"\bnetsh\b|\bnetplan\b", "network configuration change"),
    (r"set-executionpolicy", "PowerShell execution policy change"),
]

# --------------------------------------------------------------------------
# 3) SAFE / read-only commands - run without asking.
# --------------------------------------------------------------------------
SAFE_RULES = [
    r"^(ls|dir|pwd|cd|echo|cat|type|head|tail|wc|clear|cls)\b",
    r"^(whoami|hostname|date|uptime|uname|systeminfo|ver)\b",
    r"^git\s+(status|log|diff|show|branch|remote|rev-parse|describe|blame|shortlog|ls-files)\b",
    r"^(python|python3|node|npm|pip|java|go|dotnet|ruby|php|cargo)\s+(--version|-v|version)\b",
    r"^(which|where|whereis)\b",
    r"^(get-childitem|get-location|get-content|get-process|get-service|get-date|get-host)\b",
    r"^(get-computerinfo|get-volume|get-disk|get-netipaddress|get-item)\b",
    r"^(tasklist|ipconfig|ifconfig|netstat|ping|nslookup|tracert|traceroute|arp)\b",
    r"^(df|du|free|ps|env|printenv|id|groups)\b",
    r"^(grep|rg|findstr|fd)\b",
    r"^(tree|stat|file|md5sum|sha256sum)\b",
    r"^(docker|kubectl)\s+(ps|images|version|info|get)\b",
]

# --------------------------------------------------------------------------
# 4) PATH PROTECTION
# --------------------------------------------------------------------------
SYSTEM_PATHS = [
    "c:/windows", "c:/program files", "c:/program files (x86)", "c:/programdata/microsoft",
    "c:/$recycle.bin", "c:/system volume information", "c:/boot", "c:/recovery",
    "/system", "/library/systemextensions", "/bin", "/sbin", "/usr/bin", "/usr/sbin",
    "/boot", "/etc", "/var/log", "/dev", "/proc", "/sys",
]

# These files are never even read - prevents credential leaks.
SECRET_PATTERNS = [
    r"[/\\]\.ssh[/\\]id_\w+$",
    r"[/\\]\.ssh[/\\]identity$",
    r"[/\\]\.aws[/\\]credentials$",
    r"[/\\]\.gnupg[/\\]",
    r"[/\\]\.docker[/\\]config\.json$",
    r"[/\\]login data$",
    r"[/\\]cookies(\.sqlite)?$",
    r"[/\\]key4\.db$",
    r"[/\\]logins\.json$",
    r"[/\\]\.netrc$",
    r"[/\\]_netrc$",
    r"[/\\]appdata[/\\]local[/\\]microsoft[/\\]credentials[/\\]",
    r"[/\\]etc[/\\](shadow|sudoers)$",
    r"[/\\]keychains?[/\\]",
    r"[/\\]\.kube[/\\]config$",
]


@dataclass
class Verdict:
    """Risk assessment of an action."""

    risk: Risk
    reason: str = ""

    @property
    def blocked(self) -> bool:
        return self.risk is Risk.BLOCKED


@dataclass
class Action:
    """A concrete action submitted for approval."""

    tool: str
    summary: str
    verdict: Verdict
    detail: str = ""
    signature: str = ""

    def __post_init__(self) -> None:
        if not self.signature:
            self.signature = self.tool


def _normalize(command: str) -> str:
    text = command.lower()
    text = text.replace("`", "").replace("^", "")
    return re.sub(r"\s+", " ", text).strip()


def classify_command(command: str) -> Verdict:
    """Classify a shell command into a risk level."""
    if not command or not command.strip():
        return Verdict(Risk.SAFE, "empty command")

    text = _normalize(command)

    for pattern, reason in BLOCKED_RULES:
        if re.search(pattern, text, re.IGNORECASE):
            return Verdict(Risk.BLOCKED, reason)

    for pattern, reason in HIGH_RULES:
        if re.search(pattern, text, re.IGNORECASE):
            return Verdict(Risk.HIGH, reason)

    # Chained commands: evaluate every part and keep the worst.
    parts = [part.strip() for part in re.split(r"&&|\|\||;|\|", text) if part.strip()]
    if len(parts) > 1:
        worst = Verdict(Risk.SAFE, "read-only command chain")
        for part in parts:
            sub = classify_command(part)
            if sub.risk > worst.risk:
                worst = sub
        return worst

    # A redirection means a write.
    if re.search(r">{1,2}", text):
        return Verdict(Risk.MODERATE, "writes to a file")

    for pattern in SAFE_RULES:
        if re.match(pattern, text, re.IGNORECASE):
            return Verdict(Risk.SAFE, "read-only command")

    return Verdict(Risk.MODERATE, "may modify the system")


def classify_path(path, write: bool = False) -> Verdict:
    """Classify a filesystem path into a risk level.

    Both the resolved and the raw path are checked: on Windows a POSIX path like
    "/etc/passwd" resolves to "C:/etc/passwd", which would slip past the guard.
    """
    try:
        resolved = str(Path(path).expanduser().resolve())
    except (OSError, RuntimeError, ValueError):
        resolved = str(path)

    candidates = {
        resolved.replace("\\", "/").lower(),
        str(path).replace("\\", "/").lower(),
    }

    for normalized in candidates:
        for pattern in SECRET_PATTERNS:
            if re.search(pattern, normalized, re.IGNORECASE):
                return Verdict(Risk.BLOCKED, "credential or private key file")

    if write:
        for normalized in candidates:
            for system_path in SYSTEM_PATHS:
                if normalized == system_path or normalized.startswith(system_path.rstrip("/") + "/"):
                    return Verdict(Risk.BLOCKED, "protected system directory")
        return Verdict(Risk.MODERATE, "file write")

    return Verdict(Risk.SAFE, "file read")


class Guard:
    """Approval engine: allows, denies or asks based on risk level and mode."""

    def __init__(self, mode="ask", confirm=None, extra_protected=None, audit=True):
        self.mode = mode
        self.confirm: Optional[Callable[[Action], str]] = confirm
        self.session_allow = set()
        self.denied_count = 0
        self.audit = audit
        self.extra_protected = [p.replace("\\", "/").lower() for p in (extra_protected or [])]

    # ---------- main entry point ----------

    def evaluate(self, action: Action):
        """Return (allowed, reason) and write the decision to the audit log."""
        allowed, reason = self._decide(action)
        self._log(action, allowed, reason)
        if not allowed:
            self.denied_count += 1
        return allowed, reason

    def _decide(self, action: Action):
        risk = action.verdict.risk

        # 1. Hard block - no mode can get past this.
        if risk is Risk.BLOCKED:
            return False, (
                "This action is permanently blocked: " + action.verdict.reason + ". "
                "Vigil does not disable security protections, extract credentials or perform "
                "unrecoverable destruction. If it is truly needed, the user must do it themselves."
            )

        # 2. Was a session-wide allowance granted?
        #    Session allowances only cover MODERATE actions; every HIGH risk action is
        #    asked again no matter what was approved before.
        if risk <= Risk.MODERATE and action.signature in self.session_allow:
            return True, "approved for this session"

        # 3. Mode-based auto-approval.
        if risk is Risk.SAFE:
            return True, "safe action"
        if self.mode == "yolo":
            return True, "yolo mode"
        if self.mode == "auto" and risk is Risk.MODERATE:
            return True, "auto mode (moderate risk)"

        # 4. Ask the user.
        if self.confirm is None:
            return False, "no approval possible in a non-interactive session"

        answer = self.confirm(action)
        if answer == "always":
            self.session_allow.add(action.signature)
            return True, "user approved (for this session)"
        if answer == "yes":
            return True, "user approved"
        return False, "user declined"

    # ---------- shortcuts ----------

    def check_command(self, tool: str, command: str, detail: str = ""):
        verdict = classify_command(command)
        action = Action(
            tool=tool,
            summary=command.strip(),
            verdict=verdict,
            detail=detail,
            signature=tool + ":" + _signature_of(command),
        )
        return self.evaluate(action)

    def check_path(self, tool: str, path: str, write: bool = False, detail: str = "", raw=None):
        """Check a path. `raw` is the original, unresolved path the caller was given.

        Resolving happens before the guard sees the path, so a foreign-style path
        ("C:/Windows/..." on Linux, "/etc/passwd" on Windows) would otherwise be
        turned into a harmless relative path and slip through. Both forms are checked
        and the worse verdict wins.
        """
        verdict = classify_path(path, write=write)
        if raw is not None and str(raw) != str(path):
            alternative = classify_path(raw, write=write)
            if alternative.risk > verdict.risk:
                verdict = alternative
        if verdict.risk is not Risk.BLOCKED and self.extra_protected:
            try:
                normalized = str(Path(path).expanduser().resolve()).replace("\\", "/").lower()
            except (OSError, RuntimeError, ValueError):
                normalized = str(path).replace("\\", "/").lower()
            for protected in self.extra_protected:
                if normalized.startswith(protected):
                    verdict = Verdict(Risk.BLOCKED, "path protected by the user")
                    break
        action = Action(
            tool=tool,
            summary=tool + ": " + str(path),
            verdict=verdict,
            detail=detail,
            signature=tool + ":path",
        )
        return self.evaluate(action)

    def check_action(self, tool: str, summary: str, risk: Risk, reason: str = "", detail: str = ""):
        action = Action(
            tool=tool,
            summary=summary,
            verdict=Verdict(risk, reason),
            detail=detail,
            signature=tool,
        )
        return self.evaluate(action)

    # ---------- audit log ----------

    def _log(self, action: Action, allowed: bool, reason: str) -> None:
        if not self.audit:
            return
        record = {
            "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
            "tool": action.tool,
            "risk": action.verdict.risk.label,
            "risk_reason": action.verdict.reason,
            "summary": action.summary[:500],
            "allowed": allowed,
            "decision": reason,
            "mode": self.mode,
        }
        try:
            VIGIL_HOME.mkdir(parents=True, exist_ok=True)
            with open(AUDIT_LOG, "a", encoding="utf-8") as handle:
                handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        except OSError:
            pass


def _signature_of(command: str) -> str:
    """Session-allowance scope: the first word of the command (e.g. all `python ...` calls).

    The scope only applies to MODERATE actions - HIGH risk ones are asked every single
    time, so a broad signature is not dangerous.
    """
    tokens = _normalize(command).split()
    return tokens[0] if tokens else "?"


__all__ = ["Risk", "Verdict", "Action", "Guard", "classify_command", "classify_path"]
