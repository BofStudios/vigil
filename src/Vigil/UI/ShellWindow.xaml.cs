using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Tools;
using Vigil.Desktop;
using Vigil.Voice;

namespace Vigil.UI;

public enum Shape { Pill, Bar, Panel }

/// <summary>The one window. It rests as a small pill at the top of the screen,
/// opens into the ask bar when the pointer arrives or the hot key is pressed,
/// and grows into the panel when there is a conversation to show.
///
/// The window itself never changes size - it is a fixed transparent canvas and
/// only the shape inside it moves - so there is nothing for Windows to re-lay-out,
/// nothing to stutter, and no moment where a half-made window shows grey.</summary>
public partial class ShellWindow : Window
{
    const double PillW = 108, PillH = 38, BarW = 720, BarH = 68, PanelMin = 250, PanelMax = 620;
    const int HotKeyId = 0x5649;   // "VI"

    readonly Config _config;
    readonly Tray _tray;
    readonly Glow _glow = new();
    readonly VoiceController _voice;
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    readonly DispatcherTimer _restTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    Shape _shape = Shape.Pill;
    IntPtr _hwnd;
    HwndSource? _source;
    bool _hiddenByUser;
    bool _yielded;
    bool _summoned;   // brought forward on purpose: stays, even over something full screen
    IntPtr _lastForeground;
    Point? _gripDown;
    bool _quitting;

    public ShellWindow(Config config, bool startHidden, bool preview = false)
    {
        _config = config;
        InitializeComponent();
        _tray = new Tray(visible: !preview);
        _voice = new VoiceController(config);
        _hiddenByUser = startHidden;

        Loaded += (_, _) => OnLoaded();
        Shell.SizeChanged += (_, _) => Reshape();
        Shell.MouseEnter += (_, _) => { _restTimer.Stop(); if (_shape == Shape.Pill) _peekTimer.Start(); };
        Shell.MouseLeave += (_, _) => { _peekTimer.Stop(); if (CanRest) _restTimer.Start(); };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); if (_shape == Shape.Pill && Shell.IsMouseOver) GoTo(Shape.Bar); };
        _restTimer.Tick += (_, _) => { _restTimer.Stop(); if (CanRest && !Shell.IsMouseOver) GoTo(Shape.Pill); };
        PillFace.MouseLeftButtonUp += (_, _) => Summon();
        Deactivated += (_, _) => TuckAway();
        _tuckTimer.Tick += (_, _) => { _tuckTimer.Stop(); if (!IsActive) TuckAway(); };

        Grip.MouseLeftButtonDown += (_, e) => { _gripDown = e.GetPosition(this); Grip.CaptureMouse(); e.Handled = true; };
        Grip.MouseMove += OnGripMove;
        Grip.MouseLeftButtonUp += (_, e) =>
        {
            Grip.ReleaseMouseCapture();
            if (_gripDown is not null) ToggleListening();
            _gripDown = null;
            e.Handled = true;
        };

        Input.TextChanged += (_, _) => Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Input.PreviewKeyDown += OnInputKey;
        Input.GotKeyboardFocus += (_, _) => { if (_shape == Shape.Bar && Thread.Children.Count > 0) GoTo(Shape.Panel); };
        SendButton.Click += (_, _) => SubmitInput();
        StopButton.Click += (_, _) => StopEverything();
        HideButton.Click += (_, _) => HideToTray();
        MicButton.Click += (_, _) => ToggleListening();
        PreviewKeyDown += OnKey;

        _watch.Tick += (_, _) => Watch();
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange == 0) _atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 12;
        };

        // the panel is as tall as what it holds, up to a limit, then it scrolls
        _fit.Tick += (_, _) => { _fit.Stop(); FitPanel(); };
        Thread.SizeChanged += (_, _) => FitSoon();
        PlanStrip.SizeChanged += (_, _) => FitSoon();
        AskCard.SizeChanged += (_, _) => FitSoon();

        WireHost();
        WireHitArea();
        WireVoice();
        WireFooter();
        WireTray();
        WireFriend();
        BuildLevels();
        StartTrace();
    }

    // =================================================================== placement
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW);
        Centre();
        if (!Native.RegisterHotKey(_hwnd, HotKeyId, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x20))
            Toast("Ctrl+Shift+Space is taken by another app - click the pill or the tray icon instead.");
        // Ctrl + the < key (left of Z on Turkish and most European keyboards): open and listen
        if (!Native.RegisterHotKey(_hwnd, TalkKeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, VkOem102))
            Toast("Ctrl+< is taken by another app - say “Hey Vigil” or clap three times instead.");
    }

    const int TalkKeyId = 0x5650;
    const uint VkOem102 = 0xE2;

    /// <summary>Ctrl+&lt;: come forward and listen while it is held (or, after a tap, until you stop talking).</summary>
    void TalkKey()
    {
        if (!_config.IsReady)
        {
            Summon();
            return;
        }
        _hiddenByUser = false;
        _tucked = false;
        _summoned = true;
        if (_yielded) { _yielded = false; Topmost = true; }
        if (!IsVisible) Show();
        if (_shape == Shape.Pill) GoTo(Shape.Bar);
        PressedTalkKey();
    }

    /// <summary>Top centre of the primary monitor's work area, where the bar always lived.</summary>
    void Centre()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    void OnLoaded()
    {
        _watch.Start();
        if (_hiddenByUser) Hide();
        ShowFirstRun();
        // set up and ready: it waits out of sight until it is called (Ctrl+<, Ctrl+Shift+Space, the tray)
        if (_config.IsReady && !_preview && !_summoned) { _tucked = true; Hide(); }
        // only "Hey Vigil" or three claps keep anything listening; otherwise the microphone opens while Ctrl+< is held
        if (_config.EnableVoice && (_config.WakeWord || _config.ClapToWake))
            StartVoice(quiet: true);
        RefreshFooter();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            if (IsVisible && _shape != Shape.Pill && IsActive) HideToTray();
            else Summon();
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == TalkKeyId)
        {
            handled = true;
            TalkKey();
        }
        return IntPtr.Zero;
    }

    // ====================================================================== shapes
    // ================================================================ out of the way
    // Click anything else and Vigil is gone - like Spotlight. Still working, it shrinks to the pill and
    // goes once it is done. It stays while it needs you: an approval, a question, or you talking.
    bool _tucked;
    readonly DispatcherTimer _tuckTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    void TuckAway()
    {
        if (_preview || !IsVisible || IsActive) return;
        if (_approvalPending || _askPending || SetupView.Visibility == Visibility.Visible) return;
        if (_voice.Listening || _recording is { Done: false }) return;
        if (Busy || _voice.Speaker.Speaking)
        {
            // working or still talking: small, and gone when it has finished
            if (_shape != Shape.Pill) GoTo(Shape.Pill);
            _tuckTimer.Stop();
            _tuckTimer.Start();
            return;
        }
        _tuckTimer.Stop();
        _restTimer.Stop();
        _summoned = false;
        _tucked = true;
        GoTo(Shape.Pill);
        Hide();
    }

    /// <summary>After an answer, if nobody is looking at it, it does not stay on the screen.</summary>
    void TuckSoon()
    {
        if (IsActive) return;
        _tuckTimer.Stop();
        _tuckTimer.Start();
    }

    bool CanRest =>
        _shape == Shape.Bar && Input.Text.Length == 0 && !IsActive && !Busy && !_askPending && !_approvalPending
        && _voice.State is VoiceState.Off or VoiceState.Idle;

    public void GoTo(Shape shape, bool focus = false)
    {
        if (shape != _shape)
        {
            var growing = shape > _shape;
            _shape = shape;
            var (width, height) = shape switch
            {
                Shape.Pill => (PillW, PillH),
                Shape.Bar => (BarW, BarH),
                _ => (BarW, PanelHeight()),
            };
            _panelTarget = height;
            // width leads on the way out, height leads on the way in, so it never looks like a box resizing
            Shell.To(WidthProperty, width, 320, growing ? 0 : 60);
            Shell.To(HeightProperty, height, 360, growing ? 40 : 0);

            if (shape == Shape.Pill)
            {
                _summoned = false;
                Body.Fade(false, 90);
                PillFace.Fade(true, 180, 170);
            }
            else
            {
                PillFace.Fade(false, 70);
                Body.Fade(true, 220, 90);
            }
            Panel.Fade(shape == Shape.Panel, shape == Shape.Panel ? 240 : 100, shape == Shape.Panel ? 130 : 0);
        }
        if (focus) FocusInput();
    }

    void Reshape()
    {
        var w = Shell.ActualWidth;
        var h = Shell.ActualHeight;
        var radius = Math.Min(h / 2, 26);
        Shell.CornerRadius = new CornerRadius(radius);
        Inner.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, w - 2), Math.Max(0, h - 2)), radius - 1, radius - 1);
        CheckInCard.Margin = new Thickness(0, 16 + h + 14, 0, 0);
        var card = CheckInCard.Visibility == Visibility.Visible ? CheckInCard.ActualHeight + 12 : 0;
        Toasts.Margin = new Thickness(0, 16 + h + 14 + card, 0, 0);
    }

    // ================================================================ panel height
    readonly DispatcherTimer _fit = new() { Interval = TimeSpan.FromMilliseconds(45) };
    double _panelTarget;

    void FitSoon()
    {
        if (_shape == Shape.Panel && !_fit.IsEnabled) _fit.Start();
    }

    /// <summary>How tall the panel needs to be for what it is showing right now.</summary>
    double PanelHeight()
    {
        if (SetupView.Visibility == Visibility.Visible || _approvalPending) return PanelMax;
        var width = BarW - 2;
        Thread.Measure(new Size(width, double.PositiveInfinity));
        var content = Thread.DesiredSize.Height;
        if (PlanStrip.Visibility == Visibility.Visible)
        {
            PlanStrip.Measure(new Size(width, double.PositiveInfinity));
            content += PlanStrip.DesiredSize.Height;
        }
        if (AskCard.Visibility == Visibility.Visible)
        {
            AskCard.Measure(new Size(width, double.PositiveInfinity));
            content += AskCard.DesiredSize.Height + 8;
        }
        return Math.Clamp(BarH + 1 + content + Footer.Height + 4, PanelMin, PanelMax);
    }

    void FitPanel()
    {
        if (_shape != Shape.Panel) return;
        var height = PanelHeight();
        if (Math.Abs(height - _panelTarget) < 3) return;
        _panelTarget = height;
        Shell.To(HeightProperty, height, 260);
    }

    void FocusInput()
    {
        Activate();
        if (!IsActive && _hwnd != IntPtr.Zero) Native.Focus(_hwnd);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Input.Focus();
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;
        });
    }

    /// <summary>Brought forward by the hot key, the tray, a second launch, or the voice.</summary>
    public void Summon()
    {
        _hiddenByUser = false;
        _summoned = true;
        _tucked = false;
        if (_yielded)
        {
            _yielded = false;
            Topmost = true;
        }
        if (!IsVisible) Show();
        GoTo(Thread.Children.Count > 0 || !_config.IsReady ? Shape.Panel : Shape.Bar, focus: true);
    }

    void HideToTray()
    {
        _hiddenByUser = true;
        _summoned = false;
        _restTimer.Stop();
        GoTo(Shape.Pill);
        Hide();
    }

    void OnGripMove(object sender, MouseEventArgs e)
    {
        if (_gripDown is not { } down || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - down.X) + Math.Abs(now.Y - down.Y) < 5) return;
        _gripDown = null;
        Grip.ReleaseMouseCapture();
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    // ===================================================================== keys
    void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SubmitInput();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var at = Input.CaretIndex;
            Input.Text = Input.Text.Insert(at, "\n");
            Input.CaretIndex = at + 1;
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        // the talk key held down repeats; none of it should land in the box as "<<<<"
        if (e.Key == Key.OemBackslash && (_recording is { Done: false } || _holdWatch.IsEnabled))
        {
            e.Handled = true;
            return;
        }
        if (_approvalPending)
        {
            OnApprovalKey(e);
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_voice.Speaker.Speaking) { _voice.Speaker.Stop(); return; }
            if (_recording is { Done: false } open) { open.Release(); _voice.CancelListening(); return; }
            if (_voice.Listening) { _voice.CancelListening(); return; }
            if (_talking) { EndTalk(); return; }   // Esc while talking: that's all
            if (_askPending) { AnswerAsk(null); return; }
            if (_shape == Shape.Panel) { GoTo(Shape.Bar, focus: true); return; }
            if (_shape == Shape.Bar)
            {
                Input.Clear();
                GoTo(Shape.Pill);
                return;
            }
            HideToTray();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            NewConversation();
        }
    }

    // =================================================================== watching
    /// <summary>Twice a second: remember which app the person was in (so Vigil can hand
    /// focus back before it types), and get out of the way of anything full screen.</summary>
    void Watch()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _hwnd && Native.GetWindowTextLength(foreground) > 0)
            _lastForeground = foreground;
        SampleActivity(foreground);

        var fullscreen = Native.ForegroundIsFullscreen(_hwnd);
        if (fullscreen && !_yielded && !_summoned && !_approvalPending && !_askPending)
        {
            // a topmost window over a game costs that game most of its frames
            _yielded = true;
            Native.GetWindowRect(foreground, out var r);
            // the window's kind, never its title: titles carry what someone is watching or writing
            App.Note("stepping aside for full screen: " + Native.ClassOf(foreground) + " [" + r.Left + "," + r.Top + " - " + r.Right + "," + r.Bottom + "]");
            Topmost = false;
            if (IsVisible) Hide();
        }
        else if (!fullscreen && _yielded)
        {
            _yielded = false;
            Topmost = true;
            if (!_hiddenByUser && !_tucked) Show();
        }
    }

    // ===================================================================== toasts
    public void Toast(string text, Brush? colour = null)
    {
        var note = new Border
        {
            Background = (Brush)FindResource("Glass"),
            BorderBrush = (Brush)FindResource("CardRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(15, 8, 15, 9),
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 580,
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, -6),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 18,
                Foreground = colour ?? (Brush)FindResource("TextBrush"),
            },
        };
        RenderOptions.SetClearTypeHint(note, ClearTypeHint.Enabled);
        Toasts.Children.Add(note);
        note.To(OpacityProperty, 1, 220);
        ((TranslateTransform)note.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Flow.Curve });
        var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 18.0, 3.4, 8)) };
        life.Tick += (_, _) =>
        {
            life.Stop();
            note.To(OpacityProperty, 0, 260, 0, () => Toasts.Children.Remove(note));
        };
        life.Start();
        if (!IsVisible && !_yielded) _tray.Tell("Vigil", text);
    }

    // ==================================================================== the core
    Storyboard? _traceStory;
    Storyboard? _spinStory;

    void StartTrace()
    {
        // light running along the mark while Vigil works
        var travel = new DoubleAnimation(10.5, 0, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(travel, Trace);
        Storyboard.SetTargetProperty(travel, new PropertyPath(System.Windows.Shapes.Shape.StrokeDashOffsetProperty));
        _traceStory = new Storyboard { Children = { travel } };

        // the rings turn all the time - slowly at rest, quickly when there is work
        if (Motion.Reduced) return;
        DoubleAnimation Turn(double from, double to, double seconds) =>
            new(from, to, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
        var a = Turn(0, 360, 9);
        Storyboard.SetTarget(a, CoreRingA);
        Storyboard.SetTargetProperty(a, new PropertyPath("RenderTransform.Angle"));
        var b = Turn(140, -220, 14);
        Storyboard.SetTarget(b, CoreRingB);
        Storyboard.SetTargetProperty(b, new PropertyPath("RenderTransform.Angle"));
        var pill = Turn(0, 360, 10);
        Storyboard.SetTarget(pill, PillRing);
        Storyboard.SetTargetProperty(pill, new PropertyPath("RenderTransform.Angle"));
        _spinStory = new Storyboard { Children = { a, b, pill } };
        Loaded += (_, _) => _spinStory.Begin(this, true);
    }

    void Working(bool on)
    {
        if (_traceStory is null) return;
        if (on && !Motion.Reduced)
        {
            _traceStory.Begin(this, true);
            Trace.To(OpacityProperty, 1, 300);
            PillDot.Visibility = Visibility.Visible;
            _spinStory?.SetSpeedRatio(this, 4.5);
        }
        else
        {
            _traceStory.Stop(this);
            Trace.To(OpacityProperty, 0, 300);
            PillDot.Visibility = Visibility.Collapsed;
            _spinStory?.SetSpeedRatio(this, 1);
        }
        Breathe();
    }

    void Pulse(bool on)
    {
        if (on && !Motion.Reduced)
        {
            var grow = new DoubleAnimation(0.72, 1.45, TimeSpan.FromSeconds(2.2)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Flow.Curve };
            var fade = new DoubleAnimation(0.45, 0, TimeSpan.FromSeconds(2.2)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Flow.Curve };
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            PulseRing.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PulseRing.BeginAnimation(OpacityProperty, null);
            PulseRing.Opacity = 0;
        }
    }


    // ====================================================================== quit
    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _watch.Stop();
        Activity.Save();
        if (_config.Journal) Mind.Journal("closed", "Vigil was shut down");
        if (_hwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_hwnd, HotKeyId);
            Native.UnregisterHotKey(_hwnd, TalkKeyId);
        }
        _agent?.Stop();
        _agent?.Dispose();
        _voice.Dispose();
        _tray.Dispose();
        _glow.Close();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 tucks it away; quitting is in the tray menu
        if (!_quitting)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }
}
