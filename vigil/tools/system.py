"""System management tools: hardware status, processes, disks, network, applications."""

from __future__ import annotations

import os
import platform
import shutil
import socket
import subprocess
import time
from pathlib import Path

from ..security import Risk
from . import PermissionDenied, ToolContext, ToolError, ToolSpec

try:
    import psutil

    AVAILABLE = True
    MISSING_HINT = ""
except ImportError:  # pragma: no cover
    psutil = None
    AVAILABLE = False
    MISSING_HINT = "psutil is not installed (pip install psutil)"

IS_WINDOWS = platform.system() == "Windows"


def _human(size: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if size < 1024 or unit == "TB":
            return format(size, ".1f") + " " + unit
        size /= 1024
    return str(size)


# ------------------------------------------------------------- system_info
def system_info(ctx: ToolContext) -> str:
    uname = platform.uname()
    uptime_seconds = int(time.time() - psutil.boot_time())
    hours, remainder = divmod(uptime_seconds, 3600)
    minutes = remainder // 60

    memory = psutil.virtual_memory()
    cpu_percent = psutil.cpu_percent(interval=0.4)

    lines = [
        "System: " + uname.system + " " + uname.release + " (" + uname.version + ")",
        "Machine: " + uname.node + " / " + uname.machine,
        "Processor: " + (uname.processor or "unknown"),
        "Cores: " + str(psutil.cpu_count(logical=False)) + " physical, "
        + str(psutil.cpu_count(logical=True)) + " logical",
        "CPU usage: " + format(cpu_percent, ".1f") + "%",
        "RAM: " + _human(memory.used) + " / " + _human(memory.total)
        + " (" + format(memory.percent, ".1f") + "% used)",
        "Uptime: " + str(hours) + "h " + str(minutes) + "m",
        "Python: " + platform.python_version(),
        "User: " + (os.environ.get("USERNAME") or os.environ.get("USER") or "?"),
    ]

    try:
        battery = psutil.sensors_battery()
        if battery is not None:
            state = "charging" if battery.power_plugged else "on battery"
            lines.append("Battery: " + format(battery.percent, ".0f") + "% (" + state + ")")
    except (AttributeError, NotImplementedError):
        pass

    return "\n".join(lines)


# ----------------------------------------------------------- list_processes
def list_processes(ctx: ToolContext, filter_name: str = "", sort_by: str = "memory", limit: int = 20) -> str:
    rows = []
    for proc in psutil.process_iter(["pid", "name", "memory_info", "cpu_percent", "username"]):
        try:
            info = proc.info
            name = info.get("name") or "?"
            if filter_name and filter_name.lower() not in name.lower():
                continue
            memory = info.get("memory_info")
            rows.append(
                {
                    "pid": info.get("pid"),
                    "name": name,
                    "memory": getattr(memory, "rss", 0) if memory else 0,
                    "cpu": info.get("cpu_percent") or 0.0,
                    "user": (info.get("username") or "").split("\\")[-1],
                }
            )
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue

    key = "cpu" if str(sort_by).lower().startswith("cpu") else "memory"
    rows.sort(key=lambda r: r[key], reverse=True)
    rows = rows[: int(limit)]

    if not rows:
        return "No matching process."

    lines = ["PID      RAM        CPU%   USER           NAME"]
    for row in rows:
        lines.append(
            str(row["pid"]).ljust(8)
            + _human(row["memory"]).ljust(11)
            + format(row["cpu"], ".1f").ljust(7)
            + row["user"][:14].ljust(15)
            + row["name"]
        )
    return "\n".join(lines)


# ------------------------------------------------------------- kill_process
def kill_process(ctx: ToolContext, pid: int = 0, name: str = "", force: bool = False) -> str:
    targets = []
    if pid:
        try:
            targets.append(psutil.Process(int(pid)))
        except psutil.NoSuchProcess as exc:
            raise ToolError("PID " + str(pid) + " not found.") from exc
    elif name:
        for proc in psutil.process_iter(["pid", "name"]):
            try:
                if name.lower() in (proc.info.get("name") or "").lower():
                    targets.append(proc)
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        if not targets:
            raise ToolError("No process found with that name: " + name)
    else:
        raise ToolError("Provide either pid or name.")

    listing = ", ".join(str(p.pid) + ":" + _safe_name(p) for p in targets[:15])
    summary = "terminating " + str(len(targets)) + " process(es) -> " + listing
    allowed, reason = ctx.guard.check_action(
        "kill_process", summary, Risk.HIGH, "process termination", detail=summary
    )
    if not allowed:
        raise PermissionDenied(reason)

    killed, failed = [], []
    for proc in targets:
        try:
            proc.kill() if force else proc.terminate()
            killed.append(str(proc.pid))
        except (psutil.NoSuchProcess, psutil.AccessDenied) as exc:
            failed.append(str(proc.pid) + " (" + type(exc).__name__ + ")")

    result = str(len(killed)) + " process(es) terminated: " + ", ".join(killed)
    if failed:
        result += "\nFailed (may need administrator rights): " + ", ".join(failed)
    return result


def _safe_name(proc) -> str:
    try:
        return proc.name()
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        return "?"


# --------------------------------------------------------------- disk_usage
def disk_usage(ctx: ToolContext) -> str:
    lines = ["DRIVE         TOTAL       USED         FREE       USAGE"]
    for part in psutil.disk_partitions(all=False):
        try:
            usage = psutil.disk_usage(part.mountpoint)
        except (PermissionError, OSError):
            continue
        lines.append(
            part.device.ljust(14)
            + _human(usage.total).ljust(12)
            + _human(usage.used).ljust(13)
            + _human(usage.free).ljust(11)
            + format(usage.percent, ".1f") + "%"
        )
    return "\n".join(lines)


# ------------------------------------------------------------- network_info
def network_info(ctx: ToolContext, include_connections: bool = False) -> str:
    lines = ["Network interfaces:"]
    stats = psutil.net_if_stats()
    for interface, addresses in psutil.net_if_addrs().items():
        state = stats.get(interface)
        if state is not None and not state.isup:
            continue
        ips = [a.address for a in addresses if getattr(a, "family", None) == socket.AF_INET]
        if ips:
            lines.append("  " + interface + ": " + ", ".join(ips))

    counters = psutil.net_io_counters()
    lines.append("Traffic: sent " + _human(counters.bytes_sent) + ", received " + _human(counters.bytes_recv))

    if include_connections:
        lines.append("\nActive connections (first 25):")
        try:
            for conn in psutil.net_connections(kind="inet")[:25]:
                if conn.status != "ESTABLISHED":
                    continue
                local = conn.laddr.ip + ":" + str(conn.laddr.port) if conn.laddr else "?"
                remote = conn.raddr.ip + ":" + str(conn.raddr.port) if conn.raddr else "?"
                lines.append("  " + local + " -> " + remote + "  pid=" + str(conn.pid or "?"))
        except (psutil.AccessDenied, PermissionError):
            lines.append("  (listing connections requires administrator rights)")

    return "\n".join(lines)


# -------------------------------------------------------- list_installed_apps
def list_installed_apps(ctx: ToolContext, filter_name: str = "", limit: int = 60) -> str:
    entries = []
    if IS_WINDOWS:
        try:
            import winreg

            roots = [
                (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            ]
            seen = set()
            for root, subkey in roots:
                try:
                    with winreg.OpenKey(root, subkey) as key:
                        for index in range(winreg.QueryInfoKey(key)[0]):
                            try:
                                name = winreg.EnumKey(key, index)
                                with winreg.OpenKey(key, name) as app_key:
                                    display = winreg.QueryValueEx(app_key, "DisplayName")[0]
                                    try:
                                        version = winreg.QueryValueEx(app_key, "DisplayVersion")[0]
                                    except OSError:
                                        version = ""
                                    label = str(display) + ("  " + str(version) if version else "")
                                    if label not in seen:
                                        seen.add(label)
                                        entries.append(label)
                            except OSError:
                                continue
                except OSError:
                    continue
        except ImportError:
            pass
    elif platform.system() == "Darwin":
        for folder in ("/Applications", str(Path.home() / "Applications")):
            path = Path(folder)
            if path.is_dir():
                entries.extend(item.stem for item in path.glob("*.app"))
    else:
        for command in (["dpkg-query", "-f", "${binary:Package} ${Version}\n", "-W"], ["rpm", "-qa"]):
            if shutil.which(command[0]):
                try:
                    output = subprocess.run(command, capture_output=True, text=True, timeout=30)
                    entries.extend(line for line in output.stdout.splitlines() if line.strip())
                    break
                except (OSError, subprocess.TimeoutExpired):
                    continue

    if filter_name:
        entries = [item for item in entries if filter_name.lower() in item.lower()]
    entries.sort(key=str.lower)
    total = len(entries)
    entries = entries[: int(limit)]
    if not entries:
        return "No installed applications found."
    header = str(total) + " application(s) found"
    if total > len(entries):
        header += " (showing first " + str(len(entries)) + ")"
    return header + ":\n" + "\n".join("  " + item for item in entries)


# ----------------------------------------------------------------- open_app
def open_app(ctx: ToolContext, target: str) -> str:
    if not target.strip():
        raise ToolError("An application name or file path is required.")

    allowed, reason = ctx.guard.check_action(
        "open_app", "opening: " + target, Risk.MODERATE, "opening an application or file"
    )
    if not allowed:
        raise PermissionDenied(reason)

    try:
        if IS_WINDOWS:
            os.startfile(target)  # noqa: S606
        elif platform.system() == "Darwin":
            subprocess.Popen(["open", target])
        else:
            subprocess.Popen(["xdg-open", target])
    except OSError as exc:
        raise ToolError("Could not open it: " + str(exc)) from exc
    return target + " opened."


# --------------------------------------------------------------- clean_temp
def clean_temp(ctx: ToolContext, dry_run: bool = True) -> str:
    candidates = []
    for folder in filter(None, [os.environ.get("TEMP"), os.environ.get("TMP"), "/tmp"]):
        path = Path(folder)
        if path.is_dir():
            candidates.append(path)
    if not candidates:
        return "No temporary folder found."

    total_size = 0
    files = []
    cutoff = time.time() - 86400  # files older than one day
    for folder in candidates:
        for item in folder.glob("*"):
            try:
                if item.is_file() and item.stat().st_mtime < cutoff:
                    size = item.stat().st_size
                    total_size += size
                    files.append((item, size))
            except OSError:
                continue

    if not files:
        return "No old temporary files to clean."

    summary = str(len(files)) + " temporary file(s), " + _human(total_size) + " total"
    if dry_run:
        return (
            summary + " could be removed (dry run, nothing was deleted). "
            "Pass dry_run=false to actually delete them."
        )

    allowed, reason = ctx.guard.check_action(
        "clean_temp", summary + " will be deleted", Risk.HIGH, "temporary file cleanup", detail=summary
    )
    if not allowed:
        raise PermissionDenied(reason)

    deleted = 0
    freed = 0
    for item, size in files:
        try:
            item.unlink()
            deleted += 1
            freed += size
        except OSError:
            continue
    return str(deleted) + " file(s) deleted, " + _human(freed) + " freed."


TOOLS = [
    ToolSpec(
        name="system_info",
        description="Summarize the operating system, CPU, RAM, uptime and battery.",
        parameters={"type": "object", "properties": {}},
        handler=system_info,
        group="system",
        risk=Risk.SAFE,
        preview=lambda a: "system info",
    ),
    ToolSpec(
        name="list_processes",
        description="List running processes sorted by memory or CPU usage.",
        parameters={
            "type": "object",
            "properties": {
                "filter_name": {"type": "string", "description": "Filter by name"},
                "sort_by": {"type": "string", "enum": ["memory", "cpu"], "default": "memory"},
                "limit": {"type": "integer", "default": 20},
            },
        },
        handler=list_processes,
        group="system",
        risk=Risk.SAFE,
        preview=lambda a: "list processes",
    ),
    ToolSpec(
        name="kill_process",
        description="Terminate a process by PID or name. Always asks for confirmation.",
        parameters={
            "type": "object",
            "properties": {
                "pid": {"type": "integer"},
                "name": {"type": "string", "description": "Process name (partial match)"},
                "force": {"type": "boolean", "default": False},
            },
        },
        handler=kill_process,
        group="system",
        risk=Risk.HIGH,
        preview=lambda a: "kill process: " + str(a.get("name") or a.get("pid", "")),
    ),
    ToolSpec(
        name="disk_usage",
        description="Show total, used and free space for every drive.",
        parameters={"type": "object", "properties": {}},
        handler=disk_usage,
        group="system",
        risk=Risk.SAFE,
        preview=lambda a: "disk usage",
    ),
    ToolSpec(
        name="network_info",
        description="Show network interfaces, IP addresses and traffic counters.",
        parameters={
            "type": "object",
            "properties": {"include_connections": {"type": "boolean", "default": False}},
        },
        handler=network_info,
        group="system",
        risk=Risk.SAFE,
        preview=lambda a: "network info",
    ),
    ToolSpec(
        name="list_installed_apps",
        description="List the applications installed on this computer.",
        parameters={
            "type": "object",
            "properties": {
                "filter_name": {"type": "string"},
                "limit": {"type": "integer", "default": 60},
            },
        },
        handler=list_installed_apps,
        group="system",
        risk=Risk.SAFE,
        preview=lambda a: "installed apps",
    ),
    ToolSpec(
        name="open_app",
        description="Open an application, file or folder with its default program.",
        parameters={
            "type": "object",
            "properties": {"target": {"type": "string", "description": "App name, file or folder path"}},
            "required": ["target"],
        },
        handler=open_app,
        group="system",
        risk=Risk.MODERATE,
        preview=lambda a: "open: " + str(a.get("target", "")),
    ),
    ToolSpec(
        name="clean_temp",
        description="Clean temporary files older than one day. Reports only by default.",
        parameters={
            "type": "object",
            "properties": {"dry_run": {"type": "boolean", "default": True}},
        },
        handler=clean_temp,
        group="system",
        risk=Risk.MODERATE,
        preview=lambda a: "temp cleanup" + ("" if a.get("dry_run", True) else " (REAL DELETE)"),
    ),
]
