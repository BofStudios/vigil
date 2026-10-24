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

        // opening Vigil again (Start menu, a shortcut) opens it as an app
        _single = new SingleInstance(() => Dispatcher.BeginInvoke(() => _shell?.OpenHome()));
        if (!_single.First)
        {
            // already running: bring that one's window forward and leave
            _single.Summon();
            _single.Dispose();
            Shutdown();
            return;
        }

        Paths.Ensure();
        _ = Task.Run(Voice.Ducker.PutBackLeftovers);   // sound left low by a Vigil that closed mid-sentence
        var config = Config.Load();
        if (config.Migrated) { try { config.Save(); } catch (IOException) { } }
        Mind.UseRoot(config.MindFolder);
        try
        {
            Mind.Ensure();
            if (config.Journal) Mind.Journal("started", "Vigil is up");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(ex); }
        var background = e.Args.Contains("--background") || config.StartHidden;
        _shell = new ShellWindow(config, background);
        if (!background)
        {
            _shell.Show();
            if (config.IsReady) _shell.OpenHome();   // otherwise the bar is showing the first-run setup
        }
        else
        {
            // created, so the hot key and the wake phrase work, but not shown
            _shell.Show();
            _shell.Hide();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Voice.Ducker.Release();
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
