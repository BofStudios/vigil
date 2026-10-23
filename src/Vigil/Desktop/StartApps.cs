using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Vigil.Desktop;

/// <summary>Every app in the Start menu - classic programs and Store apps alike (Paint, Calculator,
/// WhatsApp, Spotify) - from Windows' own Apps folder, under the names the person sees. Store apps
/// have no .exe to start and no shortcut file; this is how Windows itself opens them.</summary>
public static class StartApps
{
    public sealed record App(string Name, string Id);

    static IReadOnlyList<App>? _cache;
    static DateTime _cachedAt;
    static readonly object Gate = new();

    /// <summary>All of them, read at most every few minutes (it takes a moment).</summary>
    public static IReadOnlyList<App> All()
    {
        lock (Gate)
        {
            if (_cache is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(5)) return _cache;
            _cache = Read();
            _cachedAt = DateTime.UtcNow;
            return _cache;
        }
    }

    /// <summary>The shell's objects want a single-threaded apartment; the tools run on the thread pool.</summary>
    static IReadOnlyList<App> Read()
    {
        var apps = new List<App>();
        var thread = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, id = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) apps.Add(new App(name.Trim(), id.Trim()));
                }
            }
            catch (Exception) { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        return apps;
    }

    static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The app a name most likely means: its exact name, then a name that contains it, then
    /// one whose Windows id contains it - "calculator" finds "Hesap Makinesi" (Microsoft.WindowsCalculator).
    /// Uninstallers and help links are never picked.</summary>
    public static App? Find(string name, IReadOnlyList<App>? apps = null)
    {
        apps ??= All();
        var want = Squash(name);
        if (want.Length == 0) return null;
        var usable = apps.Where(a => !Regex.IsMatch(a.Name, "uninstall|kaldır|help|yardım|documentation|readme|website|release notes", RegexOptions.IgnoreCase)
                                     && !a.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        string IdWords(App a) => Squash(a.Id.Split('!')[0].Split('_')[0].Split('\\').Last().Replace(".exe", ""));
        return usable.FirstOrDefault(a => Squash(a.Name) == want)
            ?? usable.Where(a => Squash(a.Name).Contains(want)).OrderBy(a => a.Name.Length).FirstOrDefault()
            ?? usable.Where(a => IdWords(a).Contains(want)).OrderBy(a => a.Name.Length).FirstOrDefault()
            ?? (want.Length >= 4 ? usable.Where(a => want.Contains(Squash(a.Name)) && Squash(a.Name).Length >= 4).OrderByDescending(a => a.Name.Length).FirstOrDefault() : null);
    }

    /// <summary>Opens it the way the Start menu does.</summary>
    public static void Launch(App app) =>
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Id) { UseShellExecute = true })?.Dispose();
}
