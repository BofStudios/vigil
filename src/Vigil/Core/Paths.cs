namespace Vigil.Core;

/// <summary>Where Vigil keeps its things. Everything lives under one folder the
/// person can open, read and delete by hand: ~/.vigil, or VIGIL_HOME.</summary>
public static class Paths
{
    public static string Home { get; private set; } = Resolve();

    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string AuditLog => Path.Combine(Home, "audit.jsonl");
    public static string Sessions => Path.Combine(Home, "sessions");
    public static string Screenshots => Path.Combine(Home, "screenshots");
    public static string Memory => Path.Combine(Home, "memory");
    public static string BrowserProfile => Path.Combine(Home, "browser");
    public static string Log => Path.Combine(Home, "vigil.log");

    static string Resolve()
    {
        var custom = Environment.GetEnvironmentVariable("VIGIL_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vigil");
    }

    /// <summary>Tests point this somewhere disposable so they never touch the real folder.</summary>
    internal static void UseHome(string folder) => Home = folder;

    public static void Ensure()
    {
        foreach (var folder in new[] { Home, Sessions, Screenshots, Memory })
            Directory.CreateDirectory(folder);
    }
}
