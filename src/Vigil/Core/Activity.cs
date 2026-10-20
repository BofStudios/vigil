using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Vigil.Core;

/// <summary>Awareness: which app is in front, and for how long - so Vigil can say "you have
/// been in VS Code for two hours, how is it going?" like a friend who sits nearby.
///
/// It records app names and minutes only. Never window titles, never what is on the screen,
/// never what is typed. Time away from the keyboard is not counted. Everything is kept in
/// Vigil Mind\Activity on this computer, and it can be switched off at any moment.</summary>
public static class Activity
{
    static readonly object Gate = new();
    static readonly Dictionary<uint, string> Names = new();
    static readonly Dictionary<string, double> Today = new(StringComparer.OrdinalIgnoreCase);
    static DateTime _day = DateTime.Now.Date;
    static DateTime _last = DateTime.MinValue;
    static DateTime _saved = DateTime.MinValue;
    static string _current = "";
    static DateTime _since = DateTime.Now;
    static bool _loaded;

    /// <summary>Away from the keyboard this long and the minutes stop counting.</summary>
    public static TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(3);

    public static string Current { get { lock (Gate) return _current; } }

    /// <summary>How long the current app has been in front, without a break.</summary>
    public static TimeSpan Streak { get { lock (Gate) return _current.Length == 0 ? TimeSpan.Zero : DateTime.Now - _since; } }

    public static bool Away => IdleTime() > IdleAfter;

    /// <summary>Called a few times a minute with the foreground window.</summary>
    public static void Sample(IntPtr foreground, IntPtr ignore)
    {
        var now = DateTime.Now;
        lock (Gate)
        {
            if (!_loaded) Load();
            if (now.Date != _day)
            {
                Save();
                Today.Clear();
                _day = now.Date;
            }

            var elapsed = _last == DateTime.MinValue ? 0 : Math.Min((now - _last).TotalSeconds, 30);
            _last = now;

            if (IdleTime() > IdleAfter || foreground == IntPtr.Zero || foreground == ignore)
            {
                if (IdleTime() > IdleAfter) { _current = ""; }
                return;
            }

            var app = NameOf(foreground);
            if (app.Length == 0) return;
            if (!string.Equals(app, _current, StringComparison.OrdinalIgnoreCase))
            {
                _current = app;
                _since = now;
            }
            Today[app] = Today.GetValueOrDefault(app) + elapsed;

            if (now - _saved > TimeSpan.FromMinutes(1)) Save();
        }
    }

    /// <summary>"Visual Studio Code" rather than "Code" - the name the app gives itself.</summary>
    static string NameOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "";
        if (Names.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description) && description.Length < 40) name = description.Trim();
            }
            catch (Exception) { }
        }
        catch (Exception) { name = ""; }
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) || name == "Windows Explorer") name = "File Explorer";
        if (Names.Count > 500) Names.Clear();
        Names[pid] = name;
        return name;
    }

    // ============================================================ the record
    static string FileFor(DateTime day) =>
        Path.Combine(Mind.ActivityDir, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt");

    static void Load()
    {
        _loaded = true;
        foreach (var (app, seconds) in Read(DateTime.Now.Date))
            Today[app] = seconds;
    }

    static List<(string App, double Seconds)> Read(DateTime day)
    {
        var file = FileFor(day);
        var result = new List<(string, double)>();
        if (!File.Exists(file)) return result;
        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var tab = line.LastIndexOf('\t');
                if (tab <= 0) continue;
                if (double.TryParse(line[(tab + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
                    result.Add((line[..tab].Trim(), minutes * 60));
            }
        }
        catch (IOException) { }
        return result;
    }

    public static void Save()
    {
        lock (Gate)
        {
            _saved = DateTime.Now;
            if (Today.Count == 0) return;
            try
            {
                Mind.Ensure();
                var lines = Today.OrderByDescending(p => p.Value)
                    .Select(p => p.Key + "\t" + (p.Value / 60).ToString("0.0", CultureInfo.InvariantCulture));
                File.WriteAllText(FileFor(_day), "# apps in front on " + _day.ToString("yyyy-MM-dd") + " (minutes)\r\n" + string.Join("\r\n", lines) + "\r\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Forget()
    {
        lock (Gate)
        {
            Today.Clear();
            _current = "";
            try { if (File.Exists(FileFor(_day))) File.Delete(FileFor(_day)); }
            catch (IOException) { }
        }
    }

    // ============================================================ in words
    public static string Duration(double seconds)
    {
        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 1) return "<1m";
        return minutes < 60 ? minutes + "m" : (minutes / 60) + "h " + (minutes % 60).ToString("00") + "m";
    }

    public static List<(string App, double Seconds)> Totals(DateTime day)
    {
        if (day.Date == DateTime.Now.Date)
            lock (Gate) return Today.OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList();
        return Read(day).OrderByDescending(p => p.Seconds).ToList();
    }

    public static string SummaryFor(DateTime day, int top = 6) =>
        string.Join(", ", Totals(day).Where(t => t.Seconds >= 60).Take(top).Select(t => t.App + " " + Duration(t.Seconds)));

    /// <summary>One line for the system prompt.</summary>
    public static string ForPrompt()
    {
        var today = SummaryFor(DateTime.Now.Date);
        var current = Current;
        if (today.Length == 0 && current.Length == 0) return "";
        var line = "AWARENESS (app names and times only): ";
        if (current.Length > 0) line += "right now " + current + " for " + Duration(Streak.TotalSeconds) + ". ";
        if (today.Length > 0) line += "Today: " + today + ".";
        return line;
    }

    // ================================================================ win32
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }
}
