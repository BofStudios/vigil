using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Win32Registry = Microsoft.Win32.Registry;
using Vigil.Core.Security;
using Vigil.Desktop;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>The state of the machine, and the things on it.</summary>
public static class SystemTools
{
    /// <summary>Processes Vigil will not end, whoever asks: the ones Windows dies without,
    /// and the ones that keep it safe.</summary>
    public static readonly HashSet<string> Untouchable = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "Memory Compression", "secure system",
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "SecurityHealthService", "SecurityHealthSystray",
        "MsSense", "SgrmBroker", "smartscreen", "Vigil",
    };

    static string SystemInfo(ToolContext ctx, ToolArgs _)
    {
        var memory = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref memory);
        var cpu = Native.CpuPercent();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var processor = Win32Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                                          "ProcessorNameString", "unknown")?.ToString()?.Trim() ?? "unknown";

        var lines = new List<string>
        {
            "System: " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")",
            "Machine: " + Environment.MachineName,
            "Processor: " + processor + " - " + Environment.ProcessorCount + " logical cores",
            "CPU usage: " + cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%",
            "RAM: " + Text.Human(memory.ullTotalPhys - memory.ullAvailPhys) + " / " + Text.Human(memory.ullTotalPhys)
                + " (" + memory.dwMemoryLoad + "% used)",
            "Uptime: " + (int)uptime.TotalHours + "h " + uptime.Minutes + "m",
            "User: " + Environment.UserName,
        };
        if (Native.GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryLifePercent <= 100)
            lines.Add("Battery: " + power.BatteryLifePercent + "% (" + (power.ACLineStatus == 1 ? "charging" : "on battery") + ")");
        return string.Join("\n", lines);
    }

    static string ListProcesses(ToolContext ctx, ToolArgs a)
    {
        var filter = a.Str("filter_name");
        var byCpu = a.Str("sort_by", "memory").StartsWith("cpu", StringComparison.OrdinalIgnoreCase);
        var limit = Math.Max(1, a.Int("limit", 20));
        var processes = Process.GetProcesses()
            .Where(p => filter.Length == 0 || p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var cpu = new Dictionary<int, double>();
        if (byCpu)
        {
            var before = processes.ToDictionary(p => p.Id, p => { try { return p.TotalProcessorTime; } catch (Exception) { return TimeSpan.Zero; } });
            Thread.Sleep(500);
            foreach (var p in processes)
            {
                try { cpu[p.Id] = (p.TotalProcessorTime - before[p.Id]).TotalMilliseconds / 5.0 / Environment.ProcessorCount; }
                catch (Exception) { cpu[p.Id] = 0; }
            }
        }

        long Memory(Process p) { try { return p.WorkingSet64; } catch (Exception) { return 0; } }
        var rows = processes
            .OrderByDescending(p => byCpu ? cpu.GetValueOrDefault(p.Id) : Memory(p))
            .Take(limit)
            .Select(p => p.Id.ToString().PadRight(8) + Text.Human(Memory(p)).PadRight(11)
                         + (byCpu ? cpu.GetValueOrDefault(p.Id).ToString("0.0", CultureInfo.InvariantCulture).PadRight(7) : "")
                         + p.ProcessName)
            .ToList();
        foreach (var p in processes) p.Dispose();
        if (rows.Count == 0) return "No matching process.";
        return "PID     RAM        " + (byCpu ? "CPU%   " : "") + "NAME\n" + string.Join("\n", rows);
    }

    static string KillProcess(ToolContext ctx, ToolArgs a)
    {
        var pid = a.Int("pid");
        var name = a.Str("name").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var force = a.Bool("force");

        List<Process> targets;
        if (pid > 0)
        {
            try { targets = new() { Process.GetProcessById(pid) }; }
            catch (ArgumentException) { throw new ToolError("PID " + pid + " not found."); }
        }
        else if (name.Length > 0)
        {
            targets = Process.GetProcesses().Where(p => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0) throw new ToolError("No process found with that name: " + name);
        }
        else throw new ToolError("Provide either pid or name.");

        var refused = targets.Where(p => Untouchable.Contains(p.ProcessName) || p.Id == Environment.ProcessId).ToList();
        if (refused.Count > 0)
        {
            var (_, why) = ctx.Guard.Evaluate(new GuardAction("kill_process",
                "end " + string.Join(", ", refused.Select(p => p.ProcessName).Distinct()),
                new Verdict(Risk.Blocked, "ending a process Windows or its security depends on")));
            throw new PermissionDenied(why);
        }

        var listing = string.Join(", ", targets.Take(15).Select(p => p.Id + ":" + p.ProcessName));
        var summary = "ending " + targets.Count + " process(es) -> " + listing;
        var (allowed, reason) = ctx.Guard.CheckAction("kill_process", summary, Risk.High, "process termination", summary);
        if (!allowed) throw new PermissionDenied(reason);

        var ended = new List<string>();
        var failed = new List<string>();
        foreach (var process in targets)
        {
            try
            {
                if (!force && process.CloseMainWindow() && process.WaitForExit(3000))
                    ended.Add(process.Id.ToString());
                else
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    ended.Add(process.Id.ToString());
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                failed.Add(process.Id + " (" + ex.Message + ")");
            }
            finally { process.Dispose(); }
        }
        var result = ended.Count + " process(es) ended: " + string.Join(", ", ended);
        if (failed.Count > 0) result += "\nFailed (may need administrator rights): " + string.Join(", ", failed);
        return result;
    }

    static string DiskUsage(ToolContext ctx, ToolArgs _)
    {
        var lines = new List<string> { "DRIVE   TOTAL       USED        FREE        USAGE" };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var used = drive.TotalSize - drive.TotalFreeSpace;
            lines.Add(drive.Name.PadRight(8) + Text.Human(drive.TotalSize).PadRight(12) + Text.Human(used).PadRight(12)
                      + Text.Human(drive.TotalFreeSpace).PadRight(12)
                      + (100.0 * used / Math.Max(1, drive.TotalSize)).ToString("0.0", CultureInfo.InvariantCulture) + "%");
        }
        return string.Join("\n", lines);
    }

    static string NetworkInfo(ToolContext ctx, ToolArgs a)
    {
        var lines = new List<string> { "Network interfaces:" };
        long sent = 0, received = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var ips = nic.GetIPProperties().UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address.ToString()).ToList();
            if (ips.Count > 0) lines.Add("  " + nic.Name + ": " + string.Join(", ", ips));
            try
            {
                var stats = nic.GetIPStatistics();
                sent += stats.BytesSent;
                received += stats.BytesReceived;
            }
            catch (NetworkInformationException) { }
        }
        lines.Add("Traffic: sent " + Text.Human(sent) + ", received " + Text.Human(received));
        if (a.Bool("include_connections"))
        {
            lines.Add("\nEstablished connections (first 25):");
            foreach (var c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                         .Where(c => c.State == TcpState.Established).Take(25))
                lines.Add("  " + c.LocalEndPoint + " -> " + c.RemoteEndPoint);
        }
        return string.Join("\n", lines);
    }

    static string ListInstalledApps(ToolContext ctx, ToolArgs a)
    {
        var filter = a.Str("filter_name");
        var limit = Math.Max(1, a.Int("limit", 60));
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            (Win32Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Win32Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Win32Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is not string display || app.GetValue("SystemComponent") is 1) continue;
                var version = app.GetValue("DisplayVersion") as string;
                seen.Add(display + (string.IsNullOrEmpty(version) ? "" : "  " + version));
            }
        }
        // what can be opened by name - the Start menu, Store apps included - comes first
        foreach (var start in StartApps.All()) seen.Add(start.Name);
        var entries = seen.Where(e => filter.Length == 0 || e.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0) return "No installed applications found.";
        var header = entries.Count + " application(s) found" + (entries.Count > limit ? " (showing first " + limit + ")" : "");
        return header + ":\n" + string.Join("\n", entries.Take(limit).Select(e => "  " + e));
    }

    /// <summary>Start Menu shortcuts are how a person names their apps: "Discord", "Spotify".</summary>
    static string? FindShortcut(string name)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var candidates = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try { candidates.AddRange(Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories)); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        var exact = candidates.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c).Equals(name, StringComparison.OrdinalIgnoreCase));
        return exact ?? candidates
            .Where(c => Path.GetFileNameWithoutExtension(c).Contains(name, StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileNameWithoutExtension(c).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => Path.GetFileNameWithoutExtension(c).Length)
            .FirstOrDefault();
    }

    /// <summary>Files that run something when opened. Opening one is running a program
    /// nobody has classified, so it is asked about every time.</summary>
    static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".scr", ".reg", ".hta", ".cpl", ".pif", ".jar", ".appref-ms",
    };

    static string OpenApp(ToolContext ctx, ToolArgs a)
    {
        var target = a.Req("target").Trim().Trim('"');
        if (target.Length == 0) throw new ToolError("An application name, file, folder or web address is required.");

        string launch;
        StartApps.App? app = null;
        var risk = Risk.Moderate;
        if (target.Contains("://") || System.Text.RegularExpressions.Regex.IsMatch(target, @"^[a-z][a-z0-9+.-]+:$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            launch = target;   // a web address, or an app's own protocol (spotify:, ms-settings:)
        else
        {
            var local = ctx.Resolve(target);
            if (File.Exists(local) || Directory.Exists(local))
            {
                launch = local;
                if (Runnable.Contains(Path.GetExtension(local))) risk = Risk.High;
            }
            else if ((app = StartApps.Find(target)) is not null)
                launch = app.Name;   // anything in the Start menu, Store apps included: "Paint", "WhatsApp"
            else launch = FindShortcut(target) ?? target;   // a desktop shortcut, or "mspaint" for the shell to find
        }

        var (allowed, reason) = ctx.Guard.CheckAction("open_app", "opening: " + launch, risk,
            risk == Risk.High ? "opening a file that runs a program" : "opening an application or file");
        if (!allowed) throw new PermissionDenied(reason);

        try
        {
            if (app is not null) StartApps.Launch(app);
            else Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true, WorkingDirectory = ctx.Cwd })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolError("Could not open '" + target + "': " + ex.Message +
                                ". Use system with action apps to see the exact names, or give the full path.");
        }
        return launch == target ? target + " opened." : target + " opened (" + Path.GetFileNameWithoutExtension(launch) + ").";
    }

    static string CleanTemp(ToolContext ctx, ToolArgs a)
    {
        var dryRun = a.Bool("dry_run", true);
        var folder = Path.GetTempPath();
        var cutoff = DateTime.Now.AddDays(-1);
        var files = new List<FileInfo>();
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
                if (file.LastWriteTime < cutoff) files.Add(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (files.Count == 0) return "No old temporary files to clean.";

        var total = files.Sum(f => f.Length);
        var summary = files.Count + " temporary file(s), " + Text.Human(total) + " total";
        if (dryRun)
            return summary + " could be removed (dry run, nothing was deleted). Pass dry_run=false to actually delete them.";

        var (allowed, reason) = ctx.Guard.CheckAction("clean_temp", summary + " will be deleted", Risk.High, "temporary file cleanup", summary);
        if (!allowed) throw new PermissionDenied(reason);

        int deleted = 0; long freed = 0;
        foreach (var file in files)
        {
            try { var size = file.Length; file.Delete(); deleted++; freed += size; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return deleted + " file(s) deleted, " + Text.Human(freed) + " freed.";
    }

    static string Now(ToolContext ctx, ToolArgs _) =>
        DateTime.Now.ToString("dddd, d MMMM yyyy HH:mm", CultureInfo.InvariantCulture) + " (" + TimeZoneInfo.Local.DisplayName + ")";

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "system_info", Group = "system", Risk = Risk.Safe,
            Description = "Summarize the operating system, CPU, RAM, uptime and battery.",
            Parameters = Object(), Handler = SystemInfo, Preview = _ => "system info",
        },
        new()
        {
            Name = "current_time", Group = "system", Risk = Risk.Safe,
            Description = "The current local date, time and time zone.",
            Parameters = Object(), Handler = Now, Preview = _ => "what time it is",
        },
        new()
        {
            Name = "list_processes", Group = "system", Risk = Risk.Safe,
            Description = "List running processes sorted by memory or CPU usage.",
            Parameters = Object(P("filter_name", Str("Filter by name")), P("sort_by", Str("", "memory", "cpu")), P("limit", Int())),
            Handler = ListProcesses, Preview = _ => "list processes",
        },
        new()
        {
            Name = "kill_process", Group = "system", Risk = Risk.High,
            Description = "End a program by name or PID (always asks first).",
            Parameters = Object(P("name", Str()), P("pid", Int()), P("force", Bool())),
            Handler = KillProcess, Preview = a => "end process: " + (a.Str("name").Length > 0 ? a.Str("name") : a.Str("pid")),
        },
        new()
        {
            Name = "disk_usage", Group = "system", Risk = Risk.Safe,
            Description = "Show total, used and free space for every drive.",
            Parameters = Object(), Handler = DiskUsage, Preview = _ => "disk usage",
        },
        new()
        {
            Name = "network_info", Group = "system", Risk = Risk.Safe,
            Description = "Show network interfaces, IP addresses and traffic counters.",
            Parameters = Object(P("include_connections", Bool())), Handler = NetworkInfo, Preview = _ => "network info",
        },
        new()
        {
            Name = "list_installed_apps", Group = "system", Risk = Risk.Safe,
            Description = "List the applications installed on this computer.",
            Parameters = Object(P("filter_name", Str()), P("limit", Int())),
            Handler = ListInstalledApps, Preview = _ => "installed apps",
        },
        new()
        {
            Name = "open_app", Group = "system", Risk = Risk.Moderate,
            Description = "Open an app by its Start-menu name, a file, a folder or a URL.",
            Parameters = Object(P("target", Str(), true)),
            Handler = OpenApp, Preview = a => "open: " + a.Str("target"),
            Finish = (a, output) =>
            {
                var target = a.Str("target");
                if (target.Contains("://")) return "It's open in your browser.";
                // "paint opened (Paint)." - the name Windows gives it reads better than what was typed
                var match = System.Text.RegularExpressions.Regex.Match(output, @"^(?<asked>.+?) opened(?: \((?<name>[^)]+)\))?\.$");
                if (!match.Success) return null;
                var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["asked"].Value;
                return name.Contains('\\') || name.Contains('/') ? "It's open." : name + " is open.";
            },
        },
        new()
        {
            Name = "clean_temp", Group = "system", Risk = Risk.Moderate,
            Description = "Clean temporary files older than one day. Reports only by default.",
            Parameters = Object(P("dry_run", Bool())),
            Handler = CleanTemp, Preview = a => "temp cleanup" + (a.Bool("dry_run", true) ? "" : " (REAL DELETE)"),
        },
    };
}
