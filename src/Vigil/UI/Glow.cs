using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Vigil.Desktop;

namespace Vigil.UI;

/// <summary>A light around the edge of the screen while Vigil holds the mouse or the
/// keyboard. Nobody should have to wonder who moved the pointer.
///
/// It lets every click through, never takes focus, and is left out of every
/// screenshot - including the ones Vigil takes to see what it is doing.</summary>
public sealed class Glow : Window
{
    readonly Border _edge;
    int _holders;

    public Glow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;

        var area = SystemParameters.WorkArea;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        var chrome = (Color)ColorConverter.ConvertFromString("#ECECEF");
        _edge = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, chrome.R, chrome.G, chrome.B)),
            BorderThickness = new Thickness(2.5),
            CornerRadius = new CornerRadius(10),
            Effect = new DropShadowEffect { Color = chrome, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.85 },
            Opacity = 0,
        };

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 6, 6, 7)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, Height - area.Bottom + 18),
            Child = new TextBlock
            {
                Text = "Vigil is using your mouse and keyboard  ·  Esc stops it",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6)),
            },
        };

        var root = new Grid();
        root.Children.Add(_edge);
        root.Children.Add(label);
        label.SetBinding(OpacityProperty, new System.Windows.Data.Binding(nameof(Opacity)) { Source = _edge });
        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Light up until the returned handle is disposed. Safe from any thread.</summary>
    public IDisposable Hold()
    {
        Dispatcher.Invoke(() =>
        {
            if (_holders++ == 0)
            {
                if (!IsVisible) Show();
                _edge.To(OpacityProperty, 1, 160);
            }
        });
        return new Release(this);
    }

    void Let()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (--_holders > 0) return;
            _holders = 0;
            // linger a moment: a run of quick actions should read as one
            _edge.To(OpacityProperty, 0, 420, 350, () => { if (_holders == 0) Hide(); });
        });
    }

    sealed class Release(Glow glow) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) glow.Let();
        }
    }
}
