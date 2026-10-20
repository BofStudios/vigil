using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vigil.Core;
using Vigil.Desktop;
using Activity = Vigil.Core.Activity;

namespace Vigil.UI;

/// <summary>Vigil as an app: today at a glance, notes, the journal and settings, in a window
/// with the system's own glass (Mica) behind it. A view onto the bar - the bar still owns the
/// agent, the voice and the settings.</summary>
public partial class HomeWindow : Window
{
    readonly ShellWindow _shell;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
    DateTime _journalDay = DateTime.Now.Date;
    string? _openNote;
    bool _armedDelete;

    public HomeWindow(ShellWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/vigil.ico"));

        NavToday.Checked += (_, _) => Show(PageToday);
        NavNotes.Checked += (_, _) => Show(PageNotes);
        NavJournal.Checked += (_, _) => Show(PageJournal);
        NavSettings.Checked += (_, _) => Show(PageSettings);
        OpenMind.Click += (_, _) => Open(Mind.Root);

        AskBox.TextChanged += (_, _) => AskHint.Visibility = AskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AskBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Ask(); };
        AskSend.Click += (_, _) => Ask();
        AskMic.Click += (_, _) => _shell.TalkFromHome();
        TalkMode.Click += (_, _) => { _shell.ToggleTalk(); DrawAwareness(); };
        SaysReply.Click += (_, _) => _shell.ReplyCheckIn(talk: false);
        SaysTalk.Click += (_, _) => _shell.ReplyCheckIn(talk: true);

        NewNote.Click += (_, _) => StartNote(null);
        NoteSave.Click += (_, _) => SaveNote();
        NoteDelete.Click += (_, _) => DeleteNote();
        NoteSearch.TextChanged += (_, _) =>
        {
            NoteSearchHint.Visibility = NoteSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            DrawNoteList();
        };
        NoteBody.TextChanged += (_, _) => NoteStatus.Text = "";
        NoteTitle.TextChanged += (_, _) => NoteStatus.Text = "";
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && PageNotes.Visibility == Visibility.Visible)
            {
                SaveNote();
                e.Handled = true;
            }
        };

        DayBack.Click += (_, _) => { _journalDay = _journalDay.AddDays(-1); DrawJournal(); };
        DayNext.Click += (_, _) => { if (_journalDay < DateTime.Now.Date) { _journalDay = _journalDay.AddDays(1); DrawJournal(); } };
        DayToday.Click += (_, _) => { _journalDay = DateTime.Now.Date; DrawJournal(); };

        ChangeBrain.Click += (_, _) => _shell.OpenBrainSetup();
        NameBox.LostFocus += (_, _) => SaveName();
        NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveName(); };

        // a maximized chrome-less window hangs its frame off the screen edges; pull the content back in
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness(0);

        _refresh.Tick += (_, _) => { if (IsVisible && PageToday.Visibility == Visibility.Visible) DrawToday(); DrawAwareness(); };
        Loaded += (_, _) => { DrawToday(); DrawAwareness(); BuildSettings(); _refresh.Start(); };
        Closing += (_, _) => SaveName();
        Closed += (_, _) => _refresh.Stop();
    }

    // ===================================================================== glass
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));          // dark title bar and frame
        var mica = 2;                                                      // DWMSBT_MAINWINDOW
        if (DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int)) == 0)
        {
            // the system draws the glass; WPF must let it through
            Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    Brush B(string key) => (Brush)FindResource(key);

    void Show(UIElement page)
    {
        foreach (var p in new UIElement[] { PageToday, PageNotes, PageJournal, PageSettings })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        page.Opacity = 0;
        page.To(OpacityProperty, 1, 220);
        if (page == PageToday) DrawToday();
        else if (page == PageNotes) { DrawNoteList(); if (_openNote is null) StartNote(Mind.ListNotes().FirstOrDefault()?.Title); }
        else if (page == PageJournal) DrawJournal();
        else BuildSettings();
    }

    static void Open(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) { }
    }

    // ===================================================================== today
    void Ask()
    {
        var text = AskBox.Text.Trim();
        if (text.Length == 0) return;
        AskBox.Clear();
        _shell.AskFromHome(text);
    }

    void DrawAwareness()
    {
        var on = _shell.Settings.Awareness;
        AwareDot.Fill = on ? B("Safe") : B("TextFaint");
        TalkModeText.Text = _shell.Talking ? "Talking · tap to end" : "Talk mode";
        AwareText.Text = on ? (Activity.Away ? "Awareness on · you're away" : "Awareness on") : "Awareness off";
    }

    void DrawToday()
    {
        var hour = DateTime.Now.Hour;
        var hello = hour < 5 ? "Working late" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        var name = _shell.Settings.UserName.Trim();
        Greeting.Text = hello + (name.Length > 0 ? ", " + name : "") + ".";
        TodayDate.Text = DateTime.Now.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);

        // right now
        if (!_shell.Settings.Awareness) { NowApp.Text = "Awareness is off"; NowFor.Text = "Turn it on in Settings to let Vigil notice."; }
        else if (Activity.Away) { NowApp.Text = "Away"; NowFor.Text = "Nobody at the keyboard for a while."; }
        else if (Activity.Current.Length == 0) { NowApp.Text = "Just started"; NowFor.Text = "Vigil is getting its bearings."; }
        else { NowApp.Text = Activity.Current; NowFor.Text = "for " + Activity.Duration(Activity.Streak.TotalSeconds) + " without a break"; }

        // what vigil said last
        var said = _shell.LastCheckIn;
        if (said.Length > 0)
        {
            SaysText.Text = said;
            SaysActions.Visibility = Visibility.Visible;
        }

        // apps today, as bars against the biggest one
        var totals = Activity.Totals(DateTime.Now.Date).Where(t => t.Seconds >= 60).Take(7).ToList();
        AppBars.Children.Clear();
        AppsEmpty.Visibility = totals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TodayTotal.Text = totals.Count == 0 ? "" : Activity.Duration(Activity.Totals(DateTime.Now.Date).Sum(t => t.Seconds)) + " in all";
        var top = totals.Count == 0 ? 1 : totals[0].Seconds;
        foreach (var (app, seconds) in totals)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.Children.Add(new TextBlock { Text = app, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)) });
            var fill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = B("CoreSweep"),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(Math.Max(0.03, seconds / top), 1),
            };
            fill.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = track });
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            var time = new TextBlock { Text = Activity.Duration(seconds), FontSize = 13, Foreground = B("TextDim"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(time, 2);
            row.Children.Add(time);
            AppBars.Children.Add(row);
        }

        // recently
        // Vigil starting and closing belong in the journal, not in "recently"
        static List<string> Worth(List<string> all, int keep) =>
            all.Where(l => !l.Contains("**started**") && !l.Contains("**closed**")).TakeLast(keep).ToList();
        var lines = Worth(Mind.JournalLines(DateTime.Now.Date, 80), 9);
        if (lines.Count == 0 && Mind.LastDayBefore(DateTime.Now.Date) is { } last) lines = Worth(Mind.JournalLines(last, 80), 6);
        RecentList.Children.Clear();
        RecentEmpty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var line in Enumerable.Reverse(lines))
            RecentList.Children.Add(JournalRow(line));
    }

    static readonly Regex JournalLine = new(@"^- (\d\d:\d\d)\s+\*\*(.+?)\*\*\s+(.*)$");

    UIElement JournalRow(string line)
    {
        var match = JournalLine.Match(line);
        var (time, kind, text) = match.Success ? (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) : ("", "", line.TrimStart('-', ' '));
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = time, FontSize = 12.5, Foreground = B("TextFaint"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var colour = kind switch
        {
            "refused" or "failed" => "High",
            "did" => "Core",
            "checked in" => "Moderate",
            _ => "TextDim",
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 1, 7, 2),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = kind, FontSize = 11, Foreground = B(colour) },
        };
        Grid.SetColumn(chip, 1);
        row.Children.Add(chip);
        var body = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        Grid.SetColumn(body, 2);
        row.Children.Add(body);
        return row;
    }

    // ===================================================================== notes
    void DrawNoteList()
    {
        var query = NoteSearch.Text.Trim();
        var notes = Mind.ListNotes();
        if (query.Length > 0)
        {
            var hits = Mind.SearchNotes(query, 200).Select(h => h.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            notes = notes.Where(n => hits.Contains(n.Title)).ToList();
        }
        NoteList.Children.Clear();
        if (notes.Count == 0)
            NoteList.Children.Add(new TextBlock
            {
                Text = query.Length > 0 ? "No note mentions that." : "No notes yet. Ask Vigil to note something down, or press +.",
                FontSize = 13, Foreground = B("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 0),
            });
        foreach (var note in notes)
        {
            var preview = "";
            try { preview = string.Join(" ", File.ReadLines(note.Path).Skip(1).Where(l => l.Trim().Length > 0 && !l.StartsWith("_")).Take(2)); }
            catch (IOException) { }
            var selected = string.Equals(note.Title, _openNote, StringComparison.OrdinalIgnoreCase);
            var item = new Button
            {
                Style = (Style)FindResource("Bare"),
                Tag = 10,
                Padding = new Thickness(12, 9, 12, 10),
                Margin = new Thickness(0, 0, 0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = selected ? new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)) : Brushes.Transparent,
                Content = new StackPanel
                {
                    Width = 220,
                    Children =
                    {
                        new TextBlock { Text = note.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = note.Changed.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture) + (preview.Length > 0 ? "  " + preview : ""),
                                        FontSize = 12, Foreground = B("TextFaint"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) },
                    },
                },
            };
            var title = note.Title;
            item.Click += (_, _) => StartNote(title);
            NoteList.Children.Add(item);
        }
    }

    void StartNote(string? title)
    {
        _armedDelete = false;
        _openNote = title;
        if (title is null)
        {
            NoteTitle.Text = "";
            NoteBody.Text = "";
            NoteStatus.Text = "New note";
            NoteTitle.Focus();
        }
        else
        {
            var text = Mind.ReadNote(title) ?? "";
            var lines = text.Replace("\r", "").Split('\n').ToList();
            if (lines.Count > 0 && lines[0].StartsWith("# ")) lines.RemoveAt(0);
            NoteTitle.Text = title;
            NoteBody.Text = string.Join("\n", lines).Trim();
            NoteStatus.Text = "";
        }
        DrawNoteList();
    }

    void SaveNote()
    {
        var title = NoteTitle.Text.Trim();
        if (title.Length == 0) { NoteStatus.Text = "Give it a title"; return; }
        try
        {
            if (_openNote is not null && !string.Equals(Mind.Slug(title), Mind.Slug(_openNote), StringComparison.OrdinalIgnoreCase))
                Mind.DeleteNote(_openNote);   // renamed
            File.WriteAllText(Mind.NoteFile(title), "# " + Mind.Slug(title) + "\r\n\r\n" + NoteBody.Text.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim() + "\r\n");
            _openNote = Mind.Slug(title);
            NoteStatus.Text = "Saved";
            DrawNoteList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { NoteStatus.Text = "Could not save: " + ex.Message; }
    }

    void DeleteNote()
    {
        if (_openNote is null) return;
        if (!_armedDelete)
        {
            _armedDelete = true;
            NoteStatus.Text = "Click delete again to remove it";
            return;
        }
        Mind.DeleteNote(_openNote);
        StartNote(Mind.ListNotes().FirstOrDefault()?.Title);
    }

    // =================================================================== journal
    void DrawJournal()
    {
        JournalDay.Text = _journalDay == DateTime.Now.Date ? "Today, " + _journalDay.ToString("d MMMM", CultureInfo.InvariantCulture)
                        : _journalDay.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
        DayNext.IsEnabled = _journalDay < DateTime.Now.Date;
        var apps = Activity.SummaryFor(_journalDay, 10);
        JournalApps.Text = apps.Length > 0 ? apps : "Nothing recorded.";
        var lines = Mind.JournalLines(_journalDay, 400);
        JournalList.Children.Clear();
        JournalEmpty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var line in lines) JournalList.Children.Add(JournalRow(line));
    }

    // =================================================================== preview
    /// <summary>Draws every page to PNG without showing the window (Vigil.exe --preview).</summary>
    internal void RenderPages(string folder)
    {
        var size = new Size(Width, Height);
        foreach (var (page, name) in new (UIElement, string)[] { (PageToday, "8-home-today"), (PageNotes, "9-home-notes"),
                                                                 (PageJournal, "10-home-journal"), (PageSettings, "11-home-settings") })
        {
            NavToday.IsChecked = page == PageToday;
            NavNotes.IsChecked = page == PageNotes;
            NavJournal.IsChecked = page == PageJournal;
            NavSettings.IsChecked = page == PageSettings;
            Show(page);
            page.BeginAnimation(OpacityProperty, null);
            page.Opacity = 1;
            DrawAwareness();
            Root.Measure(size);
            Root.Arrange(new Rect(size));
            Root.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width * 2, (int)Height * 2, 192, 192, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // roughly what Mica looks like over a dark desktop
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1A, 0x1C, 0x22), Color.FromRgb(0x12, 0x12, 0x16), 70), null, new Rect(size));
                var content = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width * 2, (int)Height * 2, 192, 192, PixelFormats.Pbgra32);
                content.Render(Root);
                dc.DrawImage(content, new Rect(size));
            }
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(file);
        }
    }

    // ================================================================== settings
    void SaveName()
    {
        var name = NameBox.Text.Trim();
        if (name != _shell.Settings.UserName) _shell.Change(c => c.UserName = name);
    }

    void BuildSettings()
    {
        var c = _shell.Settings;
        if (!NameBox.IsKeyboardFocused) NameBox.Text = c.UserName;
        BrainLine.Text = _shell.BrainDescription;
        BrainHint.Text = "Groq is free. Gemini, Claude and GPT use your own key; Ollama runs here.";

        VoiceSwitches.Children.Clear();
        VoiceSwitches.Children.Add(Switch("Listen for “Hey Vigil”", "Recognised on this computer.", _shell.WakeOn, _shell.SetWake));
        VoiceSwitches.Children.Add(Switch("Clap three times to wake", "Three even claps open Vigil and it listens.", c.ClapToWake, _shell.SetClaps));
        VoiceSwitches.Children.Add(Switch("Answer in English", "Whatever language you speak to it in.", _shell.English, _shell.SetEnglish));
        VoiceSwitches.Children.Add(Switch("Speak answers", "When you asked by voice.", c.SpeakReplies, on => _shell.Change(x => x.SpeakReplies = on)));

        AwareSwitches.Children.Clear();
        AwareSwitches.Children.Add(Switch("Know which app I'm in", "App names and minutes only - never the screen, titles or typing. Kept in Vigil Mind.",
                                          c.Awareness, on => { _shell.Change(x => x.Awareness = on); DrawAwareness(); }));
        AwareSwitches.Children.Add(Switch("Check in on me", "After a long stretch in one app, or when you come back from a break.",
                                          c.CheckIns, on => _shell.Change(x => x.CheckIns = on)));
        AwareSwitches.Children.Add(Switch("Say check-ins out loud", "Otherwise they appear quietly under the bar.",
                                          c.SpeakCheckIns, on => _shell.Change(x => x.SpeakCheckIns = on)));
        AwareSwitches.Children.Add(Choice("How often", "The least time between two check-ins.",
            new[] { 30, 50, 90, 120 }.Select(m => (m + " min", (object)m)).ToArray(), c.CheckInMinutes,
            v => _shell.Change(x => x.CheckInMinutes = (int)v)));
        AwareSwitches.Children.Add(ActionRow("Forget today's activity", "Deletes what was recorded today.", "Forget", () => { _shell.ForgetToday(); DrawToday(); }));
        AwareSwitches.Children.Add(Switch("Keep a journal", "What you asked and what Vigil did, in Vigil Mind\\Journal.",
                                          c.Journal, on => _shell.Change(x => x.Journal = on)));

        ModeRow.Children.Clear();
        foreach (var (mode, label) in new[] { ("ask", "Ask first"), ("auto", "Auto"), ("yolo", "Yolo") })
        {
            var on = c.ApprovalMode == mode;
            var b = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = label,
                Padding = new Thickness(16, 7, 16, 8),
                Margin = new Thickness(0, 0, 6, 0),
                Background = on ? new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)) : Brushes.Transparent,
                Foreground = on ? B("TextBrush") : B("TextDim"),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)),
                BorderThickness = new Thickness(1),
            };
            var m = mode;
            b.Click += (_, _) => { _shell.SetApproval(m); BuildSettings(); };
            ModeRow.Children.Add(b);
        }

        SystemSwitches.Children.Clear();
        SystemSwitches.Children.Add(Switch("Start with Windows", "Vigil comes up quietly when you sign in.", Startup.Enabled, on =>
        {
            try { Startup.Set(on); } catch (Exception) { }
        }));
        SystemSwitches.Children.Add(ActionRow("Vigil Mind", Mind.Root, "Open", () => Open(Mind.Root)));
        SystemSwitches.Children.Add(ActionRow("What Vigil did", "Every decision, in the audit log.", "Open", () => Open(Paths.AuditLog)));
    }

    FrameworkElement Row(string title, string detail, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 0, 20, 0),
            Children =
            {
                new TextBlock { Text = title, FontSize = 14 },
                new TextBlock { Text = detail, FontSize = 12, Foreground = B("TextFaint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) },
            },
        });
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return grid;
    }

    FrameworkElement Switch(string title, string detail, bool on, Action<bool> changed)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return Row(title, detail, box);
    }

    FrameworkElement Choice(string title, string detail, (string Label, object Value)[] options, object current, Action<object> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in options)
        {
            var on = Equals(value, current);
            var b = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = label,
                Padding = new Thickness(11, 5, 11, 6),
                Margin = new Thickness(4, 0, 0, 0),
                Background = on ? new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)) : Brushes.Transparent,
                Foreground = on ? B("TextBrush") : B("TextDim"),
            };
            var v = value;
            b.Click += (_, _) => { changed(v); BuildSettings(); };
            row.Children.Add(b);
        }
        return Row(title, detail, row);
    }

    FrameworkElement ActionRow(string title, string detail, string label, Action act)
    {
        var b = new Button { Style = (Style)FindResource("Key"), Content = label, Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(0) };
        b.Click += (_, _) => act();
        return Row(title, detail, b);
    }
}
