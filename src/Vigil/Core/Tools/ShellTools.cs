using System.Diagnostics;
using System.Text;
using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>Running commands, and the working directory they run in.</summary>
public static class ShellTools
{
    const int DefaultTimeout = 90;
    const int MaxTimeout = 900;

    static string? _powershell;

    static string PowerShellPath()
    {
        if (_powershell is not null) return _powershell;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), "pwsh.exe");
                if (folder.Length > 0 && File.Exists(candidate)) return _powershell = candidate;
            }
            catch (ArgumentException) { }
        }
        return _powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    }

    static string RunCommand(ToolContext ctx, ToolArgs a)
    {
        var command = a.Req("command").Trim();
        if (command.Length == 0) throw new ToolError("Empty command.");

        var workDir = a.Has("cwd") && a.Str("cwd").Trim().Length > 0 ? ctx.Resolve(a.Str("cwd")) : ctx.Cwd;
        if (!Directory.Exists(workDir)) throw new ToolError("Working directory not found: " + workDir);

        var shell = a.Str("shell", "auto").ToLowerInvariant();
        if (shell is "auto" or "") shell = "powershell";
        if (shell is not ("powershell" or "cmd")) throw new ToolError("Unknown shell: " + shell + " (use powershell or cmd).");

        var verdict = Rules.ClassifyCommand(command);
        var detail = "shell: " + shell + "\ndirectory: " + workDir;
        if (verdict.Reason.Length > 0) detail += "\nassessment: " + verdict.Reason;

        var (allowed, reason) = ctx.Guard.CheckCommand("run_command", command, detail);
        if (!allowed) throw new PermissionDenied(reason);

        var timeout = Math.Clamp(a.Int("timeout", DefaultTimeout), 1, MaxTimeout);
        var start = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["NO_COLOR"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        if (shell == "powershell")
        {
            start.FileName = PowerShellPath();
            // the machine's execution policy stands as it is: no -ExecutionPolicy Bypass here
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; " + command })
                start.ArgumentList.Add(arg);
        }
        else
        {
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = "/d /s /c \"chcp 65001>nul & " + command + "\"";
        }

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ToolError("Could not run the command: " + ex.Message);
        }
        process.StandardInput.Close();   // anything waiting for input gets end-of-file, not a hang
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (!process.WaitForExit(200))
        {
            if (ctx.Cancel.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (ctx.Cancel.IsCancellationRequested) throw new OperationCanceledException(ctx.Cancel);
                throw new ToolError("The command did not finish within " + timeout + " seconds and was stopped. " +
                                    "Raise the timeout for long jobs, or start them in the background.");
            }
        }
        process.WaitForExit();

        string Out() { lock (stdout) return stdout.ToString().Trim(); }
        string Err() { lock (stderr) return stderr.ToString().Trim(); }

        var parts = new List<string> { "exit code: " + process.ExitCode };
        if (Out().Length > 0) parts.Add("stdout:\n" + Out());
        if (Err().Length > 0) parts.Add("stderr:\n" + Err());
        if (parts.Count == 1) parts.Add("(no output)");
        return Text.Truncate(string.Join("\n\n", parts), ctx.Config.MaxToolOutput);
    }

    static string ChangeDir(ToolContext ctx, ToolArgs a)
    {
        var target = ctx.Resolve(a.Req("path"));
        if (!Directory.Exists(target)) throw new ToolError("Directory not found: " + target);
        ctx.Cwd = target;
        return "Working directory: " + ctx.Cwd;
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "run_command", Group = "terminal", Risk = Risk.Moderate,
            Description = "Run a PowerShell (default) or cmd command. Nothing interactive.",
            Parameters = Object(P("command", Str(), true), P("cwd", Str()), P("timeout", Int("Seconds, default 90")),
                                P("shell", Str("", "powershell", "cmd"))),
            Handler = RunCommand,
            RiskFn = a => Rules.ClassifyCommand(a.Str("command")).Risk,
            Preview = a => a.Str("command"),
        },
        new()
        {
            Name = "change_dir", Group = "terminal", Risk = Risk.Safe,
            Description = "Change the working folder.",
            Parameters = Object(P("path", Str(), true)),
            Handler = ChangeDir, Preview = a => "cd " + a.Str("path"),
        },
        new()
        {
            Name = "current_dir", Group = "terminal", Risk = Risk.Safe,
            Description = "Show the current working directory.",
            Parameters = Object(),
            Handler = (ctx, _) => ctx.Cwd, Preview = _ => "pwd",
        },
    };
}
