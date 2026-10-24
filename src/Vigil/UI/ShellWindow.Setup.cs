using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Desktop;
using Forms = System.Windows.Forms;

namespace Vigil.UI;

/// <summary>The first screen, the footer chips and the tray menu: everything a person
/// can change without opening a file.</summary>
public partial class ShellWindow
{
    Route _route = Routes.Free;
    bool _planShown;

    // ================================================================== setup
    void ShowFirstRun()
    {
        if (_config.IsReady) return;
        ShowSetup();
        if (!_hiddenByUser)
        {
            Show();
            GoTo(Shape.Panel);
        }
    }

    void ShowSetup(string error = "")
    {
        _route = Routes.Get(_config.Provider);
        SetupView.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        _planShown = PlanStrip.Visibility == Visibility.Visible;
        PlanStrip.Visibility = Visibility.Collapsed;
        DrawSetup();
        SetupError.Text = error;
        SetupError.Visibility = error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoTo(Shape.Panel);
    }

    void HideSetup()
    {
        SetupView.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        if (_planShown) PlanStrip.Visibility = Visibility.Visible;
    }

    void DrawSetup()
    {
        SetupTabs.Children.Clear();
        foreach (var route in Routes.All)
        {
            var tab = new Button
            {
                Style = (Style)FindResource("Chip"),
                Padding = new Thickness(14, 6, 14, 7),
                Tag = 10,
                Margin = new Thickness(0),
                Content = new TextBlock
                {
                    Inlines =
                    {
                        new System.Windows.Documents.Run(route.Name) { FontWeight = FontWeights.SemiBold, FontSize = 12.5 },
                        new System.Windows.Documents.Run("  " + route.Badge) { Foreground = B("TextFaint"), FontSize = 11 },
                    },
                },
            };
            if (route == _route)
            {
                tab.Background = B("Surface3");
                tab.Foreground = B("TextBrush");
            }
            tab.Click += (_, _) => { _route = route; DrawSetup(); };
            SetupTabs.Children.Add(tab);
        }

        SetupBlurb.Text = _route.Blurb;
        SetupModels.Children.Clear();
        foreach (var (id, note) in _route.Models)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(new TextBlock { Text = id, FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono"), FontSize = 12 });
            row.Children.Add(new TextBlock { Text = "   " + note, FontSize = 12, Foreground = B("TextFaint") });
            SetupModels.Children.Add(row);
        }
        SetupLink.Content = _route.LinkText + "  ↗";
        SetupKey.Visibility = _route.NeedsKey ? Visibility.Visible : Visibility.Collapsed;
        SetupHost.Visibility = _route.NeedsKey ? Visibility.Collapsed : Visibility.Visible;
        SetupHost.Text = _config.OllamaHost;
        SetupKey.Password = "";
        SetupGo.Content = _route.NeedsKey ? "Connect" : "Use Ollama";
        SetupError.Visibility = Visibility.Collapsed;
    }

    async void Connect()
    {
        var candidate = _config.Clone();
        candidate.Provider = _route.Key;
        var key = SetupKey.Password.Trim();
        switch (_route.Key)
        {
            case "anthropic": candidate.AnthropicApiKey = key; break;
            case "openai": candidate.OpenaiApiKey = key; break;
            case "gemini": candidate.GeminiApiKey = key; break;
            case "ollama": candidate.OllamaHost = SetupHost.Text.Trim().Length > 0 ? SetupHost.Text.Trim() : Config.DefaultOllamaHost; break;
            default:
                candidate.ApiKey = key;
                candidate.Model = Brains.Get(candidate.Brain).Model;
                break;
        }
        if (_route.NeedsKey && key.Length == 0)
        {
            SetupError.Text = "Paste your key first.";
            SetupError.Visibility = Visibility.Visible;
            return;
        }

        SetupGo.IsEnabled = false;
        SetupGo.Content = "Checking…";
        SetupError.Visibility = Visibility.Collapsed;
        try
        {
            var provider = ProviderFactory.Build(candidate);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await provider.ListModelsAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is ProviderException or OperationCanceledException or HttpRequestException)
        {
            SetupError.Text = ex is OperationCanceledException ? "No answer in time. Check your connection." : ex.Message;
            SetupError.Visibility = Visibility.Visible;
            SetupGo.IsEnabled = true;
            SetupGo.Content = _route.NeedsKey ? "Connect" : "Use Ollama";
            return;
        }

        Apply(candidate);
        SetupGo.IsEnabled = true;
        HideSetup();
        RebuildAgent();
        Toast("Connected. Ask away" + (_voice.WakeListening ? " - or just say “Hey Vigil”." : "."));
        FocusInput();
    }

    /// <summary>Copies settings over and saves them.</summary>
    void Apply(Config changed)
    {
        foreach (var property in typeof(Config).GetProperties().Where(p => p.CanWrite && p.CanRead && p.GetIndexParameters().Length == 0))
            property.SetValue(_config, property.GetValue(changed));
        Save();
    }

    void Save()
    {
        try { _config.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast("Could not save settings: " + ex.Message, B("High"));
        }
    }

    // ================================================================= footer
    void WireFooter()
    {
        SetupGo.Click += (_, _) => Connect();
        SetupLink.Click += (_, _) => OpenUrl(_route.Link);
        SetupKey.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Connect(); };

        BrainChip.Click += (_, _) => Menu(BrainChip, Brains.InOrder.Select(brain => Option(
            brain.Name, brain.Tagline, brain.Key == _config.Brain, () => SetBrain(brain.Key))));

        ModelChip.Click += (_, _) => Menu(ModelChip, ModelOptions());

        FolderChip.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Where should Vigil work?", InitialDirectory = _agent?.Context.Cwd ?? "" };
            if (dialog.ShowDialog(this) == true && _agent is not null)
            {
                _agent.Context.Cwd = dialog.FolderName;
                _agent.RefreshSystemPrompt();
                RefreshFooter();
            }
        };

        VoiceChip.Click += (_, _) => Menu(VoiceChip, new[]
        {
            Option("Listen for “Hey Vigil”", "the phrase is recognised on this computer", _config.WakeWord && _voice.WakeListening, ToggleWake),
            Option("Clap three times to wake", "three even claps open Vigil and it listens", _config.ClapToWake, ToggleClaps),
            Option("Answer in English", "whatever language you speak to it in", AnswersInEnglish, ToggleEnglish),
            Option("Speak answers", "when you asked by voice", _config.SpeakReplies, () => { _config.SpeakReplies = !_config.SpeakReplies; Save(); }),
            Option("Talk mode", "every answer spoken; hold Ctrl+< to talk, or to interrupt", _talking, ToggleTalk),
            Option("Lower other sounds while we talk", "music and videos step back while you hold Ctrl+< and while Vigil speaks",
                   _config.DuckOthers, () => { _config.DuckOthers = !_config.DuckOthers; Save(); }),
            Option("Voicebox voice…", Voice.Voicebox.SeenRunning
                       ? (_config.VoiceboxVoice.Length > 0 ? _config.VoiceboxVoice : "Voicebox's default") + " - natural, on this PC, no key"
                       : "free and local (voicebox.sh) - start or get it",
                   Voice.Voicebox.SeenRunning && _config.TtsEngine is "auto" or "voicebox", VoiceboxMenu),
            Option("ElevenLabs voice…", _voice.Speaker.ElevenLabsReady ? Voice.ElevenLabs.NameOf(_config.ElevenLabsVoice) + " - the most natural voice"
                                                                       : "add your ElevenLabs key in the app's Settings",
                   _voice.Speaker.ElevenLabsReady, OpenHome),
            Option("Best voice available", LocalVoiceLine(),
                   _config.TtsEngine is "auto" or "groq" or "piper", () => SetVoiceEngine("auto")),
            Option("Windows voice only", VoiceName() + " - on this computer", _config.TtsEngine == "windows", () => SetVoiceEngine("windows")),
            Option("OpenAI voice", ProviderFactory.CloudVoice(_config) is null ? "needs an OpenAI key" : "the most natural, every language",
                   _config.TtsEngine == "cloud", () => SetVoiceEngine("cloud"), enabled: ProviderFactory.CloudVoice(_config) is not null),
            Option("Turn on the natural voice…", "accept Groq's Orpheus terms once, in your browser", false, () =>
            {
                OpenUrl(Voice.Speaker.GroqTermsUrl);
                _config.GroqVoiceRefused = false;
                Save();
                _voice.Speaker.RetryNaturalVoice();
            }, enabled: ProviderFactory.GroqVoice(_config) is not null),
            Option("Say something", "hear how it sounds", false, () => _voice.Speaker.Say(Hello(), SpeechLanguage)),
        });

        ModeDot.Click += (_, _) => Menu(ModeDot, new[]
        {
            Option("Ask", "asks before anything that changes the system", _config.ApprovalMode == "ask", () => SetMode("ask")),
            Option("Auto", "routine changes run; risky ones are asked", _config.ApprovalMode == "auto", () => SetMode("auto")),
            Option("Yolo", "everything runs except what is blocked", _config.ApprovalMode == "yolo", () => SetMode("yolo")),
            Option("Mouse, keyboard and screen: asked once per task", "in every mode, no exceptions - Esc takes them back", true, () => { }, enabled: false),
        });

        NewChip.Click += (_, _) => NewConversation();
        HomeChip.Click += (_, _) => OpenHome();
        TalkChip.Click += (_, _) => ToggleTalk();
        WorkChip.Click += (_, _) => Menu(WorkChip, new[]
        {
            Option(InWorkMode ? "Stop work mode" : "Start work mode",
                   InWorkMode ? (_config.WorkGoal.Length > 0 ? "working on: " + _config.WorkGoal : "watching since " + _work!.Started.ToString("HH:mm"))
                           : "watches how it goes, steps in with advice", InWorkMode, ToggleWork),
            Option("Every 10 minutes", "", _config.WorkMinutes == 10, () => SetWorkMinutes(10)),
            Option("Every 20 minutes", "", _config.WorkMinutes == 20, () => SetWorkMinutes(20)),
            Option("Every 30 minutes", "", _config.WorkMinutes == 30, () => SetWorkMinutes(30)),
            Option("What it looks at", "app names and minutes only - never the screen or what you type", false, () => { }, enabled: false),
        });
        QuietChip.Click += (_, _) => Menu(QuietChip, new[]
        {
            Option("Do not disturb for an hour", "no check-ins or advice; it answers when you ask", false, () => SetQuiet(true, TimeSpan.FromHours(1))),
            Option("Until tomorrow morning", "back on at 8:00", false, () => SetQuiet(true, UntilMorning)),
            Option("Until I turn it off", "", _config.Quiet && _config.QuietUntil is null, () => SetQuiet(true)),
            Option("Off", "check-ins and advice as usual", !_config.Quiet, () => SetQuiet(false)),
        });
    }

    /// <summary>What "best voice" means on this computer right now.</summary>
    string LocalVoiceLine() => _voice.Speaker.LocalVoiceReady
        ? _voice.Speaker.LocalVoiceName + " - a neural voice on this computer"
        : "a neural voice on this PC if installed, else Groq, OpenAI or Windows";

    bool AnswersInEnglish => !string.Equals(_config.ReplyLanguage, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>The language Vigil speaks in when it is not answering a spoken question.</summary>
    string SpeechLanguage => AnswersInEnglish ? "en" : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    string Hello() => !AnswersInEnglish && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr"
        ? "Merhaba, ben Vigil. Emrindeyim."
        : "Good evening. Vigil here, online and at your service. Shall I show you what I can do?";

    void ToggleClaps()
    {
        _config.ClapToWake = !_config.ClapToWake;
        Save();
        if (_config.ClapToWake && !_voice.On) StartVoice(quiet: true);
        _voice.Refresh();
        Toast(_config.ClapToWake ? "Clap three times and Vigil comes forward, listening." : "Claps no longer wake Vigil.");
    }

    void ToggleEnglish()
    {
        _config.ReplyLanguage = AnswersInEnglish ? "auto" : "en";
        Save();
        _agent?.RefreshSystemPrompt();
        Toast(AnswersInEnglish ? "Vigil answers in English from now on." : "Vigil answers in the language you use.");
    }

    string VoiceName()
    {
        var voice = _voice.Speaker.VoiceFor(SpeechLanguage) ?? _voice.Speaker.VoiceFor("en");
        return voice is null ? "none installed" : voice.DisplayName;
    }

    IEnumerable<MenuItem> ModelOptions()
    {
        var known = _config.Provider switch
        {
            "anthropic" => Routes.ClaudeModels,
            "openai" => Routes.OpenAiModels,
            "gemini" => Routes.GeminiModels,
            "ollama" => new Dictionary<string, string> { [_config.OllamaModel] = "installed with ollama pull" },
            _ => Routes.GroqModels,
        };
        var items = known.Select(m => Option(m.Key, m.Value, m.Key == _config.ActiveModel, () => SetModel(m.Key))).ToList();
        if (!known.ContainsKey(_config.ActiveModel))
            items.Insert(0, Option(_config.ActiveModel, "current", true, () => { }));
        items.Add(Option("Change provider or key…", Routes.Get(_config.Provider).Name, false, () => ShowSetup()));
        return items;
    }

    void SetBrain(string key)
    {
        _config.Brain = key;
        if (_config.Provider == "groq") _config.Model = Brains.Get(key).Model;
        Save();
        _agent?.SetBrain(key);
        RefreshFooter();
        var brain = Brains.Get(key);
        if (brain.Warning.Length > 0) Toast(brain.Warning, B("Moderate"));
    }

    void SetModel(string model)
    {
        _config.ActiveModel = model;
        Save();
        if (_agent is not null) _agent.Provider.Model = model;
        RefreshFooter();
    }

    void SetMode(string mode)
    {
        _config.ApprovalMode = mode;
        Save();
        if (_agent is not null)
        {
            _agent.Guard.Mode = mode;
            _agent.RefreshSystemPrompt();
        }
        RefreshFooter();
    }

    void ToggleWake()
    {
        _config.WakeWord = !(_config.WakeWord && _voice.WakeListening);
        Save();
        if (_config.WakeWord)
        {
            _voice.Stop();
            StartVoice(quiet: false);
        }
        else
        {
            _voice.Refresh();
            Toast("No longer listening for “Hey Vigil”. Hold Ctrl+< to talk.");
        }
        RefreshFooter();
    }

    void SetVoiceEngine(string engine)
    {
        _config.TtsEngine = engine;
        _voice.Speaker.RetryNaturalVoice();
        _voice.Speaker.RetryVoicebox();
        Save();
        _voice.Speaker.Say(Hello(), SpeechLanguage);
    }

    void SetWorkMinutes(int minutes)
    {
        _config.WorkMinutes = minutes;
        Save();
        Toast("Work mode steps in every " + minutes + " minutes.");
    }

    /// <summary>The voices made in Voicebox, to pick from - or how to get it, when it is not running.</summary>
    async void VoiceboxMenu()
    {
        if (!await Voice.Voicebox.RunningAsync(_config.VoiceboxUrl))
        {
            if (Voice.Voicebox.Installed() is { } exe)
            {
                OpenUrl(exe);
                Toast("Starting Voicebox. Once it is up, pick a voice in it and Vigil will speak with it.");
                _voice.Speaker.RetryVoicebox();
            }
            else
            {
                OpenUrl(Voice.Voicebox.Site);
                Toast("Voicebox is a free voice studio that runs on this PC. Install it, download a voice model in it " +
                      "(Kokoro is light and quick), make or pick a voice - Vigil speaks with it as soon as it is running.");
            }
            return;
        }
        IReadOnlyList<Voice.Voicebox.Profile> profiles;
        try { profiles = await Voice.Voicebox.ProfilesAsync(_config.VoiceboxUrl); }
        catch (ProviderException ex)
        {
            Toast(ex.Message, B("Moderate"));
            return;
        }
        var items = new List<MenuItem>
        {
            Option("Voicebox's default voice", "the one set in Voicebox", _config.VoiceboxVoice.Length == 0, () => SetVoiceboxVoice("")),
        };
        items.AddRange(profiles.Select(p => Option(p.Name, p.Language, p.Name.Equals(_config.VoiceboxVoice, StringComparison.OrdinalIgnoreCase),
                                                   () => SetVoiceboxVoice(p.Name))));
        if (profiles.Count == 0)
            items.Add(Option("No voices yet", "make or pick one in Voicebox first", false, () => { }, enabled: false));
        Menu(VoiceChip, items);
    }

    void SetVoiceboxVoice(string name)
    {
        _config.VoiceboxVoice = name;
        if (_config.TtsEngine is not ("auto" or "voicebox")) _config.TtsEngine = "auto";
        _voice.Speaker.RetryVoicebox();
        Save();
        _voice.Speaker.Say(Hello(), SpeechLanguage);
    }

    void RefreshFooter()
    {
        BrainText.Text = Brains.Get(_config.Brain).Name;
        var model = _agent?.Provider.Model ?? _config.ActiveModel;
        ModelText.Text = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        var cwd = _agent?.Context.Cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        FolderText.Text = System.IO.Path.GetFileName(cwd.TrimEnd('\\')) is { Length: > 0 } name ? name : cwd;
        FolderChip.ToolTip = cwd;

        var listening = _voice.WakeListening;
        VoiceChipText.Text = listening ? "Hey Vigil" : "Hold Ctrl+<";
        VoiceGlyph.Text = "\uE720";
        VoiceChip.Foreground = listening ? B("Core") : B("TextDim");
        TalkChip.Foreground = _talking ? B("Core") : B("TextDim");
        QuietChip.Foreground = _config.Quiet ? B("Moderate") : B("TextDim");
        QuietChip.ToolTip = "Do not disturb: " + QuietLine;
        TalkChipText.Text = _talking ? "Talking" : "Talk";
        WorkChip.Foreground = InWorkMode ? B("Core") : B("TextDim");
        WorkChipText.Text = InWorkMode ? "Working" : "Work";

        var tokens = _agent?.Provider.TotalTokens ?? 0;
        TokenText.Text = tokens == 0 ? "" : tokens < 1000 ? tokens + " tok" : (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k tok";

        ModeFill.Fill = B(_config.ApprovalMode switch { "ask" => "Safe", "yolo" => "High", _ => "Moderate" });
        ModeText.Text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(_config.ApprovalMode);
        ModeDot.ToolTip = "Approval: " + _config.ApprovalMode + " - mouse, keyboard and screen are asked once per task";
    }

    // ================================================================== menus
    MenuItem Option(string title, string subtitle, bool check, Action pick, bool enabled = true)
    {
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        if (subtitle.Length > 0)
            header.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = B("TextFaint"), Margin = new Thickness(0, 1, 0, 0) });
        var item = new MenuItem { Header = header, IsChecked = check, IsEnabled = enabled };
        item.Click += (_, _) => pick();
        return item;
    }

    void Menu(FrameworkElement anchor, IEnumerable<MenuItem> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top, VerticalOffset = -6 };
        foreach (var item in items) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    // =================================================================== tray
    void WireTray()
    {
        // the icon by the clock opens Vigil as an app; the bar has its own keys
        _tray.Clicked += () => Dispatcher.BeginInvoke(OpenHome);
        _tray.MenuOpening += menu =>
        {
            menu.Items.Clear();
            menu.Items.Add(Tray.Item("Open Vigil", () => Dispatcher.BeginInvoke(OpenHome)));
            menu.Items.Add(Tray.Item("Show the bar        Ctrl+Shift+Space", () => Dispatcher.BeginInvoke(Summon)));
            menu.Items.Add(Tray.Item("Talk                       Ctrl+<", () => Dispatcher.BeginInvoke(() => { Summon(); ToggleListening(); })));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Tray.Item("Listen for “Hey Vigil”", () => Dispatcher.BeginInvoke(ToggleWake), _config.WakeWord && _voice.WakeListening));
            menu.Items.Add(Tray.Item("Clap three times to wake", () => Dispatcher.BeginInvoke(ToggleClaps), _config.ClapToWake));
            menu.Items.Add(Tray.Item("Answer in English", () => Dispatcher.BeginInvoke(ToggleEnglish), AnswersInEnglish));
            menu.Items.Add(Tray.Item("Talk mode", () => Dispatcher.BeginInvoke(ToggleTalk), _talking));
            menu.Items.Add(Tray.Item("Work mode", () => Dispatcher.BeginInvoke(ToggleWork), InWorkMode));
            menu.Items.Add(Tray.Item("Do not disturb", () => Dispatcher.BeginInvoke(() => SetQuiet(!_config.Quiet)), _config.Quiet));
            menu.Items.Add(Tray.Item("Speak answers", () => Dispatcher.BeginInvoke(() => { _config.SpeakReplies = !_config.SpeakReplies; Save(); }), _config.SpeakReplies));
            menu.Items.Add(Tray.Item("Start with Windows", () =>
            {
                try { Startup.Set(!Startup.Enabled); }
                catch (Exception ex) { Dispatcher.BeginInvoke(() => Toast("Could not change that: " + ex.Message)); }
            }, Startup.Enabled));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Tray.Item("Open Vigil Mind", () => OpenUrl(Mind.Root)));
            menu.Items.Add(Tray.Item("Open Vigil's settings folder", () => OpenUrl(Paths.Home)));
            menu.Items.Add(Tray.Item("What Vigil did (audit log)", () => { if (File.Exists(Paths.AuditLog)) OpenUrl(Paths.AuditLog); }));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Tray.Item("Quit Vigil", () => Dispatcher.BeginInvoke(Quit)));
        };
    }
}
