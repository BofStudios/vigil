using System.Text.RegularExpressions;

namespace Vigil.Core.Security;

/// <summary>How much an action can hurt.</summary>
public enum Risk
{
    /// <summary>Read-only or trivially reversible: runs on its own.</summary>
    Safe = 0,
    /// <summary>Changes the system: asks unless the mode says otherwise.</summary>
    Moderate = 1,
    /// <summary>Hard to reverse or destructive: always asks.</summary>
    High = 2,
    /// <summary>Never runs.</summary>
    Blocked = 3,
}

public static class RiskText
{
    public static string Label(this Risk risk) => risk switch
    {
        Risk.Safe => "safe",
        Risk.Moderate => "moderate",
        Risk.High => "high",
        _ => "blocked",
    };
}

public sealed record Verdict(Risk Risk, string Reason = "")
{
    public bool Blocked => Risk == Risk.Blocked;
}

/// <summary>The rules. Vigil operates the computer; it does not weaken its security.
///
/// "böyle security duvarını falan aşmıcak" - it will not get past a security wall.
/// That sentence is why this file exists, and nothing classified BLOCKED here ever
/// runs, in any mode, whoever asks.</summary>
public static class Rules
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static (Regex Pattern, string Reason) R(string pattern, string reason) => (new Regex(pattern, Options), reason);

    // ----------------------------------------------------------------------
    // 1) HARD BLOCKS - disabling security controls, stealing credentials,
    //    destroying recovery options, wiping forensic traces.
    // ----------------------------------------------------------------------
    public static readonly IReadOnlyList<(Regex Pattern, string Reason)> Blocked = new[]
    {
        // --- disabling security software ---
        R(@"set-mppreference[^\n]*-disable\w*\s+\$?true", "disabling Windows Defender protection"),
        R(@"add-mppreference[^\n]*-exclusionpath", "hiding a folder from Defender scans"),
        R(@"(uninstall|remove)-windowsfeature[^\n]*defender", "removing Windows Defender"),
        R(@"netsh\s+advfirewall\s+set\s+\w+\s+state\s+off", "turning off the Windows firewall"),
        R(@"set-netfirewallprofile[^\n]*-enabled\s+false", "disabling a firewall profile"),
        R(@"(systemctl|service)\s+(stop|disable)\s+(ufw|firewalld|apparmor)", "disabling the Linux firewall"),
        R(@"setenforce\s+0", "disabling SELinux"),
        R(@"enablelua[^\n]*/d\s*0", "disabling UAC (User Account Control)"),
        R(@"consentpromptbehavioradmin[^\n]*/d\s*0", "disabling the UAC elevation prompt"),
        R(@"set-executionpolicy[^\n]*(unrestricted|bypass)[^\n]*(machine|localmachine)",
          "removing machine-wide PowerShell script protection"),
        R(@"bcdedit[^\n]*(nointegritychecks|testsigning|safeboot|recoveryenabled\s+no)",
          "altering Windows boot or code-signing protections"),
        R(@"(taskkill|stop-process|kill|pskill|sc\s+stop|stop-service|net\s+stop)[^\n]*(msmpeng|nissrv|securityhealth|mssense|sense\b|windefend|wscsvc|mpssvc|mpdefendercoreservice|smartscreen)",
          "stopping Windows security software"),
        R(@"(sc\s+config|set-service)[^\n]*(windefend|wscsvc|mpssvc|securityhealthservice)[^\n]*(disabled|start=\s*disabled)",
          "disabling a Windows security service"),
        R(@"csrutil\s+disable", "disabling macOS System Integrity Protection"),
        R(@"spctl\s+--master-disable", "disabling macOS Gatekeeper"),

        // --- destroying backups and recovery (ransomware behaviour) ---
        R(@"vssadmin[^\n]*delete\s+shadows", "deleting system restore points"),
        R(@"wbadmin[^\n]*delete\s+(catalog|backup|systemstatebackup)", "deleting Windows backups"),
        R(@"wmic[^\n]*shadowcopy[^\n]*delete", "deleting shadow copies"),
        R(@"reagentc[^\n]*/disable", "disabling the Windows recovery environment"),

        // --- disk and root destruction ---
        R(@"\brm\s+(-[a-z]*\s+)*-[a-z]*[rf][a-z]*\s+(-[a-z]+\s+)*/(\s|$|\*)", "deleting the root directory"),
        R(@"\bmkfs(\.\w+)?\b", "formatting a disk"),
        R(@"\bformat\s+[a-z]:", "formatting a drive"),
        R(@"\bdiskpart\b[^\n]*\bclean\b", "wiping a disk (diskpart clean)"),
        R(@"\bdd\s+[^\n]*of=/dev/(sd|nvme|disk)", "raw writing to a disk device"),
        R(@"del\s+/[a-z\s/]*\s+[a-z]:\\\*", "mass-deleting a drive root"),
        R(@"remove-item[^\n]*\s[a-z]:\\?\s*(-recurse|$)", "mass-deleting a drive root"),
        R(@":\(\)\s*\{\s*:\|:&\s*\}\s*;:", "fork bomb"),

        // --- credential theft ---
        R(@"\bmimikatz\b|\bsekurlsa\b|\blsadump\b", "credential dumping tool"),
        R(@"(procdump|rundll32)[^\n]*lsass", "LSASS memory dump"),
        R(@"reg\s+save\s+hk(lm|ey_local_machine)\\(sam|security|system)", "copying the password database"),
        R(@"\bsecretsdump\b|\bhashdump\b|\bcreddump\b", "credential dumping tool"),
        R(@"(cat|type|copy|cp)\s+[^\n]*/etc/shadow", "reading or copying /etc/shadow"),

        // --- covering tracks ---
        R(@"wevtutil\s+cl\b", "clearing Windows event logs"),
        R(@"clear-eventlog\b", "clearing Windows event logs"),
        R(@"(rm|shred|truncate)[^\n]*/var/log", "destroying system logs"),
        R(@"\bcipher\s+/w", "making deleted data unrecoverable"),
        R(@"history\s+-c\b", "clearing shell history"),

        // --- download-and-execute (unverified remote code) ---
        R(@"(curl|wget)\s[^\n|]*\|\s*(sudo\s+)?(ba|z|s|fi)?sh", "piping downloaded code straight into a shell"),
        R(@"(iwr|invoke-webrequest|invoke-restmethod|irm)[^\n]*\|\s*(iex|invoke-expression)",
          "piping downloaded code straight into a shell"),
        R(@"iex\s*\(\s*new-object\s+net\.webclient", "piping downloaded code straight into a shell"),

        // --- system-wide privilege handover ---
        R(@"icacls\s+[a-z]:\\?\s+/grant\s+\w+:\(?f\)?", "granting full control over a drive root"),
        R(@"takeown\s+/f\s+[a-z]:\\?\s+/r", "taking ownership of a drive root"),
        R(@"chmod\s+-r\s+777\s+/(\s|$)", "opening all permissions on the root directory"),
    };

    // ----------------------------------------------------------------------
    // 2) HIGH RISK - allowed, but explicit approval every single time.
    // ----------------------------------------------------------------------
    public static readonly IReadOnlyList<(Regex Pattern, string Reason)> High = new[]
    {
        R(@"\brm\s+(-[a-z]*\s+)*-[a-z]*[rf]", "recursive or forced delete"),
        R(@"remove-item[^\n]*-(recurse|force)", "recursive or forced delete"),
        R(@"\b(rmdir|rd)\s+/s", "deleting a directory tree"),
        R(@"del\s+/[sqf]", "bulk file delete"),
        R(@"\b(shutdown|restart-computer|stop-computer|reboot|halt|poweroff)\b", "shutting down or rebooting"),
        R(@"\btaskkill\b|\bkill\s+-9\b|\bpkill\b|stop-process", "terminating processes"),
        R(@"\breg\s+(delete|add)\b|remove-itemproperty|set-itemproperty[^\n]*hk(lm|cu)", "registry modification"),
        R(@"\bsc\s+(delete|stop|config)\b|stop-service|set-service", "Windows service modification"),
        R(@"\bnet\s+(user|localgroup)\b[^\n]*(/add|/delete)", "user account modification"),
        R(@"\bschtasks\b[^\n]*/(create|delete)|register-scheduledtask", "creating or deleting a scheduled task"),
        R(@"\b(useradd|userdel|usermod|passwd)\b", "user account modification"),
        R(@"\b(pip|pip3|npm|yarn|pnpm|winget|choco|brew|apt|apt-get|dnf|pacman)\s+(install|add|upgrade|remove|uninstall)",
          "installing or removing packages"),
        R(@"\bgit\s+push\b[^\n]*(--force|-f)\b", "force push"),
        R(@"\bgit\s+reset\s+--hard\b|\bgit\s+clean\s+-[a-z]*f", "permanently discarding local changes"),
        R(@"\bsudo\b|\brunas\b|start-process[^\n]*-verb\s+runas", "running with administrator privileges"),
        R(@"\bnetsh\b|\bnetplan\b", "network configuration change"),
        R(@"set-executionpolicy", "PowerShell execution policy change"),
    };

    // ----------------------------------------------------------------------
    // 3) SAFE - read-only commands, run without asking.
    // ----------------------------------------------------------------------
    public static readonly IReadOnlyList<Regex> Safe = new[]
    {
        @"^(ls|dir|pwd|cd|echo|cat|type|head|tail|wc|clear|cls)\b",
        @"^(whoami|hostname|date|uptime|uname|systeminfo|ver)\b",
        @"^git\s+(status|log|diff|show|branch|remote|rev-parse|describe|blame|shortlog|ls-files)\b",
        @"^(python|python3|node|npm|pip|java|go|dotnet|ruby|php|cargo)\s+(--version|-v|version)\b",
        @"^(which|where|whereis)\b",
        @"^(get-childitem|get-location|get-content|get-process|get-service|get-date|get-host)\b",
        @"^(get-computerinfo|get-volume|get-disk|get-netipaddress|get-item)\b",
        @"^(tasklist|ipconfig|ifconfig|netstat|ping|nslookup|tracert|traceroute|arp)\b",
        @"^(df|du|free|ps|env|printenv|id|groups)\b",
        @"^(grep|rg|findstr|fd)\b",
        @"^(tree|stat|file|md5sum|sha256sum)\b",
        @"^(docker|kubectl)\s+(ps|images|version|info|get)\b",
    }.Select(pattern => new Regex(pattern, Options)).ToArray();

    // ----------------------------------------------------------------------
    // 4) PATH PROTECTION
    // ----------------------------------------------------------------------
    public static readonly IReadOnlyList<string> SystemPaths = new[]
    {
        "c:/windows", "c:/program files", "c:/program files (x86)", "c:/programdata/microsoft",
        "c:/$recycle.bin", "c:/system volume information", "c:/boot", "c:/recovery",
        "/system", "/library/systemextensions", "/bin", "/sbin", "/usr/bin", "/usr/sbin",
        "/boot", "/etc", "/var/log", "/dev", "/proc", "/sys",
    };

    /// <summary>Never even read: keeps credentials from leaking into a conversation.</summary>
    public static readonly IReadOnlyList<Regex> Secrets = new[]
    {
        @"[/\\]\.ssh[/\\]id_\w+$",
        @"[/\\]\.ssh[/\\]identity$",
        @"[/\\]\.aws[/\\]credentials$",
        @"[/\\]\.gnupg[/\\]",
        @"[/\\]\.docker[/\\]config\.json$",
        @"[/\\]login data$",
        @"[/\\]cookies(\.sqlite)?$",
        @"[/\\]key4\.db$",
        @"[/\\]logins\.json$",
        @"[/\\]\.netrc$",
        @"[/\\]_netrc$",
        @"[/\\]appdata[/\\]local[/\\]microsoft[/\\]credentials[/\\]",
        @"[/\\]etc[/\\](shadow|sudoers)$",
        @"[/\\]keychains?[/\\]",
        @"[/\\]\.kube[/\\]config$",
        // Vigil's own settings hold the API keys
        @"[/\\]\.vigil[/\\]config\.json$",
    }.Select(pattern => new Regex(pattern, Options)).ToArray();

    // ----------------------------------------------------------------------
    // 5) ALWAYS ASK - taking the mouse, the keyboard or the screen.
    //
    // Everything else can be waved through by auto or yolo. These cannot: when
    // something else is driving your pointer and typing on your behalf, a
    // standing permission is not consent, it is a blank cheque. So every task
    // that reaches for them asks first - once, the way Parsec asks when someone
    // wants to take over - and hands them back when it ends. No mode skips it,
    // "always allow" is not offered, and the answer has to come from a person
    // at the keyboard or mouse - never from a voice, which a video playing
    // nearby could fake.
    // ----------------------------------------------------------------------
    public static readonly IReadOnlySet<string> AlwaysAsk = new HashSet<string>
    {
        "mouse_click",
        "mouse_move",
        "mouse_scroll",
        "click_on",
        "keyboard_type",
        "press_keys",
        "screen_capture",
        "clipboard",
    };

    static string Normalize(string command)
    {
        var text = command.ToLowerInvariant().Replace("`", "").Replace("^", "");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>The first word: the scope of a session-wide allowance (every `python ...`).
    /// Only MODERATE actions can be allowed for a session, so a broad scope is not dangerous.</summary>
    public static string SignatureOf(string command)
    {
        var tokens = Normalize(command).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 ? tokens[0] : "?";
    }

    public static Verdict ClassifyCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new Verdict(Risk.Safe, "empty command");

        var text = Normalize(command);

        foreach (var (pattern, reason) in Blocked)
            if (pattern.IsMatch(text))
                return new Verdict(Risk.Blocked, reason);

        foreach (var (pattern, reason) in High)
            if (pattern.IsMatch(text))
                return new Verdict(Risk.High, reason);

        // Chained commands: judge every part and keep the worst.
        var parts = Regex.Split(text, @"&&|\|\||;|\|").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count > 1)
        {
            var worst = new Verdict(Risk.Safe, "read-only command chain");
            foreach (var part in parts)
            {
                var sub = ClassifyCommand(part);
                if (sub.Risk > worst.Risk)
                    worst = sub;
            }
            return worst;
        }

        // A redirection means a write.
        if (Regex.IsMatch(text, ">{1,2}"))
            return new Verdict(Risk.Moderate, "writes to a file");

        foreach (var pattern in Safe)
            if (pattern.IsMatch(text))
                return new Verdict(Risk.Safe, "read-only command");

        return new Verdict(Risk.Moderate, "may modify the system");
    }

    /// <summary>Both the resolved and the raw path are checked: "/etc/passwd" resolves to
    /// "C:\etc\passwd" on Windows and would otherwise slip past.</summary>
    public static Verdict ClassifyPath(string path, bool write = false)
    {
        string resolved;
        try { resolved = Path.GetFullPath(ExpandHome(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { resolved = path; }

        var candidates = new HashSet<string>
        {
            resolved.Replace('\\', '/').ToLowerInvariant(),
            path.Replace('\\', '/').ToLowerInvariant(),
        };

        foreach (var normalized in candidates)
            foreach (var pattern in Secrets)
                if (pattern.IsMatch(normalized))
                    return new Verdict(Risk.Blocked, "credential or private key file");

        if (!write)
            return new Verdict(Risk.Safe, "file read");

        foreach (var normalized in candidates)
            foreach (var system in SystemPaths)
                if (normalized == system || normalized.StartsWith(system.TrimEnd('/') + "/", StringComparison.Ordinal))
                    return new Verdict(Risk.Blocked, "protected system directory");

        return new Verdict(Risk.Moderate, "file write");
    }

    public static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Environment.ExpandEnvironmentVariables(path);
    }
}
