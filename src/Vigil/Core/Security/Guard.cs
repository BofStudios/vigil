using System.Text.Json;

namespace Vigil.Core.Security;

public enum Answer { No, Yes, Always }

/// <summary>A concrete action put up for approval.</summary>
public sealed record GuardAction(string Tool, string Summary, Verdict Verdict, string Detail = "", string Signature = "")
{
    public string Scope => string.IsNullOrEmpty(Signature) ? Tool : Signature;

    /// <summary>Whether the person may wave this through for the rest of the session.</summary>
    public bool CanAlwaysAllow => Verdict.Risk == Risk.Moderate && !Rules.AlwaysAsk.Contains(Tool);
}

/// <summary>Allows, refuses or asks, by risk and by mode. One per conversation.</summary>
public sealed class Guard
{
    readonly HashSet<string> _sessionAllow = new();
    readonly List<string> _protected;
    readonly object _lock = new();

    public string Mode { get; set; }
    /// <summary>Asks the person. Blocks the calling (worker) thread until they answer.
    /// Null means there is nobody to ask, and everything that needs asking is refused.</summary>
    public Func<GuardAction, Answer>? Confirm { get; set; }
    public bool Audit { get; set; }
    public int DeniedCount { get; private set; }

    public Guard(string mode = "ask", Func<GuardAction, Answer>? confirm = null,
                 IEnumerable<string>? extraProtected = null, bool audit = true)
    {
        Mode = mode;
        Confirm = confirm;
        Audit = audit;
        _protected = (extraProtected ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/').ToLowerInvariant())
            .ToList();
    }

    // --------------------------------------------------------------- entry
    public (bool Allowed, string Reason) Evaluate(GuardAction action)
    {
        var (allowed, reason) = Decide(action);
        Log(action, allowed, reason);
        if (!allowed)
            DeniedCount++;
        return (allowed, reason);
    }

    (bool, string) Decide(GuardAction action)
    {
        var risk = action.Verdict.Risk;

        // 1. Hard block. No mode, no answer, no argument gets past this.
        if (risk == Risk.Blocked)
            return (false,
                "This action is permanently blocked: " + action.Verdict.Reason + ". " +
                "Vigil does not disable security protections, extract credentials or perform " +
                "unrecoverable destruction. If it is truly needed, the user must do it themselves.");

        // 2. Taking the pointer, the keyboard or the screen is asked every time.
        if (Rules.AlwaysAsk.Contains(action.Tool))
        {
            if (Confirm is null)
                return (false, "Control of the mouse, keyboard and screen is always confirmed, " +
                               "and there is nobody to ask in a non-interactive session.");
            var answer = Confirm(action);
            // deliberately not remembered: "always" is not on offer here
            return answer is Answer.Yes or Answer.Always
                ? (true, "user allowed this one")
                : (false, "user declined");
        }

        // 3. A session-wide allowance covers MODERATE only; HIGH is asked again every time.
        lock (_lock)
            if (risk <= Risk.Moderate && _sessionAllow.Contains(action.Scope))
                return (true, "approved for this session");

        // 4. The mode.
        if (risk == Risk.Safe)
            return (true, "safe action");
        if (Mode == "yolo")
            return (true, "yolo mode");
        if (Mode == "auto" && risk == Risk.Moderate)
            return (true, "auto mode (moderate risk)");

        // 5. Ask.
        if (Confirm is null)
            return (false, "no approval possible in a non-interactive session");

        switch (Confirm(action))
        {
            case Answer.Always when action.CanAlwaysAllow:
                lock (_lock) _sessionAllow.Add(action.Scope);
                return (true, "user approved (for this session)");
            case Answer.Always:
            case Answer.Yes:
                return (true, "user approved");
            default:
                return (false, "user declined");
        }
    }

    // ----------------------------------------------------------- shortcuts
    public (bool Allowed, string Reason) CheckCommand(string tool, string command, string detail = "") =>
        Evaluate(new GuardAction(tool, command.Trim(), Rules.ClassifyCommand(command), detail,
                                 tool + ":" + Rules.SignatureOf(command)));

    /// <summary>`raw` is the path as the model wrote it. Both it and the resolved path are
    /// judged and the worse verdict wins.</summary>
    public (bool Allowed, string Reason) CheckPath(string tool, string path, bool write = false,
                                                   string detail = "", string? raw = null) =>
        Evaluate(new GuardAction(tool, tool + ": " + path, JudgePath(path, write, raw), detail, tool + ":path"));

    /// <summary>The verdict on a path without asking anybody anything.</summary>
    public Verdict JudgePath(string path, bool write = false, string? raw = null)
    {
        var verdict = Rules.ClassifyPath(path, write);
        if (raw is not null && raw != path)
        {
            var alternative = Rules.ClassifyPath(raw, write);
            if (alternative.Risk > verdict.Risk)
                verdict = alternative;
        }

        if (verdict.Risk != Risk.Blocked && _protected.Count > 0)
        {
            string normalized;
            try { normalized = Path.GetFullPath(Rules.ExpandHome(path)).Replace('\\', '/').ToLowerInvariant(); }
            catch (Exception) { normalized = path.Replace('\\', '/').ToLowerInvariant(); }
            if (_protected.Any(p => normalized.StartsWith(p, StringComparison.Ordinal)))
                verdict = new Verdict(Risk.Blocked, "path protected by the user");
        }
        return verdict;
    }

    public (bool Allowed, string Reason) CheckAction(string tool, string summary, Risk risk,
                                                     string reason = "", string detail = "") =>
        Evaluate(new GuardAction(tool, summary, new Verdict(risk, reason), detail, tool));

    // ------------------------------------------------------------ audit log
    void Log(GuardAction action, bool allowed, string reason)
    {
        if (!Audit)
            return;
        var record = new Dictionary<string, object>
        {
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["tool"] = action.Tool,
            ["risk"] = action.Verdict.Risk.Label(),
            ["risk_reason"] = action.Verdict.Reason,
            ["summary"] = action.Summary.Length > 500 ? action.Summary[..500] : action.Summary,
            ["allowed"] = allowed,
            ["decision"] = reason,
            ["mode"] = Mode,
        };
        try
        {
            Directory.CreateDirectory(Paths.Home);
            lock (typeof(Guard))
                File.AppendAllText(Paths.AuditLog, JsonSerializer.Serialize(record) + "\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
