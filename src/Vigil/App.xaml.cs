using System.Windows;
using System.Windows.Threading;
using Vigil.Core;
using Vigil.Desktop;
using Vigil.UI;

namespace Vigil;

public partial class App : Application
{
    SingleInstance? _single;
    ShellWindow? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };

        var preview = Array.IndexOf(e.Args, "--preview");
        if (preview >= 0)
        {
            // draws every state to PNG files and leaves; nothing appears on screen
            var folder = preview + 1 < e.Args.Length ? e.Args[preview + 1] : Path.Combine(Paths.Home, "preview");
            new ShellWindow(Config.Load(), startHidden: true, preview: true).RenderPreview(folder);
            Shutdown();
            return;
        }

        _single = new SingleInstance(() => Dispatcher.BeginInvoke(() => _shell?.Summon()));
        if (!_single.First)
        {
            // already running: bring that one forward and leave
            _single.Summon();
            _single.Dispose();
            Shutdown();
            return;
        }

        Paths.Ensure();
        var config = Config.Load();
        var background = e.Args.Contains("--background") || config.StartHidden;
        _shell = new ShellWindow(config, background);
        if (!background) _shell.Show();
        else
        {
            // created, so the hot key and the wake phrase work, but not shown
            _shell.Show();
            _shell.Hide();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        try { _shell?.Toast("Something went wrong: " + e.Exception.Message); } catch (Exception) { }
    }

    public static void Note(string text)
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            File.AppendAllText(Paths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\n");
        }
        catch (Exception) { }
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            File.AppendAllText(Paths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + "\n\n");
        }
        catch (Exception) { }
    }
}
