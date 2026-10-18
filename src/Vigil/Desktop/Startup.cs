using Microsoft.Win32;

namespace Vigil.Desktop;

/// <summary>Starting with Windows - off until the person turns it on. A per-user
/// Run entry: no administrator rights, nothing machine-wide, and removing it is
/// one click in Task Manager's Startup tab.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Vigil";

    static string Command => "\"" + Environment.ProcessPath + "\" --background";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(Name, Command);
        else if (key.GetValue(Name) is not null) key.DeleteValue(Name);
    }
}

/// <summary>One Vigil at a time. A second launch wakes the first and leaves.</summary>
public sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\BOFStudios.Vigil";
    const string EventName = @"Local\BOFStudios.Vigil.Summon";

    readonly Mutex _mutex;
    readonly EventWaitHandle _summon;
    readonly RegisteredWaitHandle? _waiting;

    public bool First { get; }

    public SingleInstance(Action onSummon)
    {
        _mutex = new Mutex(true, MutexName, out var created);
        First = created;
        _summon = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (First)
            _waiting = ThreadPool.RegisterWaitForSingleObject(_summon, (_, _) => onSummon(), null, Timeout.Infinite, false);
    }

    /// <summary>Wakes the Vigil that is already running.</summary>
    public void Summon() => _summon.Set();

    public void Dispose()
    {
        _waiting?.Unregister(null);
        if (First)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _summon.Dispose();
    }
}
