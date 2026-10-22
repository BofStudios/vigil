using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vigil.Desktop;

namespace Vigil.UI;

/// <summary>The little pill at the bottom of the screen while you talk: a live waveform of your
/// voice, so it is plain that Vigil hears you, then "Understanding…" once you let go. It never
/// takes focus and clicks go straight through it - whatever you were typing in keeps the keyboard.</summary>
public sealed class TalkPill : Window
{
    const int BarCount = 28;
    const double BarWidth = 3, BarGap = 3, BarMax = 26, BarMin = 3;

    readonly Rectangle[] _bars = new Rectangle[BarCount];
    readonly double[] _history = new double[BarCount];
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly TextBlock _label = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) };
    readonly Ellipse _dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly ScaleTransform _dotScale = new(1, 1);
    Func<float> _level = () => 0f;
    bool _hearing;
    double _phase;

    public TalkPill(FrameworkElement theme)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;

        var sweep = (Brush)theme.FindResource("CoreSweep");
        var waves = new Canvas { Width = BarCount * (BarWidth + BarGap) - BarGap, Height = BarMax, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle { Width = BarWidth, Height = BarMin, RadiusX = 1.5, RadiusY = 1.5, Fill = sweep };
            Canvas.SetLeft(bar, i * (BarWidth + BarGap));
            Canvas.SetTop(bar, (BarMax - BarMin) / 2);
            _bars[i] = bar;
            waves.Children.Add(bar);
        }

        _dot.Fill = (Brush)theme.FindResource("Core");
        _dot.RenderTransformOrigin = new Point(0.5, 0.5);
        _dot.RenderTransform = _dotScale;
        _label.Foreground = (Brush)theme.FindResource("TextBrush");
        _label.FontFamily = (FontFamily)theme.FindResource("Sans");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 20, 0) };
        row.Children.Add(new Border { Width = 18, Child = _dot, Margin = new Thickness(0, 0, 12, 0) });
        row.Children.Add(waves);
        row.Children.Add(_label);

        Content = new Border
        {
            Height = 54,
            CornerRadius = new CornerRadius(27),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x11, 0x15)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(24),   // room for the shadow
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5, Color = Colors.Black },
            Child = row,
        };
        _tick.Tick += (_, _) => Frame();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // never activated, never in Alt+Tab, clicks fall through to what is underneath
        Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
    }

    /// <summary>You are talking: the waveform follows `level` (0..1, as the microphone reports it).</summary>
    public void Listen(Func<float> level, string text)
    {
        _level = level;
        _hearing = false;
        _label.Text = text;
        Array.Clear(_history);
        Place();
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        _tick.Start();
    }

    /// <summary>Let go: the words are being made out.</summary>
    public void Hearing()
    {
        if (!IsVisible) return;
        _hearing = true;
        _label.Text = "Understanding…";
    }

    public void Away()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (Opacity > 0.01) return;   // shown again meanwhile
            _tick.Stop();
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Bottom centre of the screen the pointer is on - where your eyes are.</summary>
    void Place()
    {
        UpdateLayout();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = DesiredSize;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - size.Width) / 2;
        Top = area.Bottom - size.Height - 40;
    }

    void Frame()
    {
        _phase += 0.22;
        if (_hearing)
        {
            // a wave travelling through the bars while the words are worked out
            for (var i = 0; i < BarCount; i++)
                SetBar(i, 0.22 + 0.32 * (0.5 + 0.5 * Math.Sin(_phase * 1.4 - i * 0.45)));
            _dotScale.ScaleX = _dotScale.ScaleY = 0.85 + 0.15 * Math.Sin(_phase);
            return;
        }

        // the microphone's level sits around 0.5 in a quiet room and nears 0.9 for a voice: stretch that
        var raw = Math.Clamp((_level() - 0.5) * 2.6, 0, 1);
        // newest on the right, the rest slide left: a short trail of what was said
        Array.Copy(_history, 1, _history, 0, BarCount - 1);
        _history[^1] = _history[^1] * 0.35 + raw * 0.65;
        for (var i = 0; i < BarCount; i++)
        {
            // a little life even in silence, so it never looks frozen
            var idle = 0.06 + 0.04 * Math.Sin(_phase + i * 0.7);
            SetBar(i, Math.Max(idle, _history[i]));
        }
        _dotScale.ScaleX = _dotScale.ScaleY = 1 + 0.5 * raw;
    }

    void SetBar(int i, double amount)
    {
        var height = BarMin + (BarMax - BarMin) * Math.Clamp(amount, 0, 1);
        var bar = _bars[i];
        bar.Height += (height - bar.Height) * 0.5;
        Canvas.SetTop(bar, (BarMax - bar.Height) / 2);
    }

    /// <summary>For `--preview`: one frame with a made-up voice, nothing on screen.</summary>
    internal FrameworkElement RenderFrame(float[] levels, string text, bool hearing)
    {
        _label.Text = text;
        _hearing = hearing;
        foreach (var level in levels)
        {
            _level = () => level;
            Frame();
        }
        for (var k = 0; k < 6; k++) Frame();
        return (FrameworkElement)Content;
    }
}
