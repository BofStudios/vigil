using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Vigil.Desktop;

namespace Vigil.UI;

/// <summary>Only what you can see can be clicked.
///
/// The window is a fixed 780 x 740 transparent canvas (so it never resizes or
/// flashes grey), but its soft shadows have a little alpha far around the shape,
/// and Windows sends every click on a pixel with any alpha to the window. That
/// made a dark, click-eating box around the pill. Now the window lets every
/// click through (WS_EX_TRANSPARENT) unless the pointer is over the shell or a
/// card under it; a quick look at the pointer turns that on and off.</summary>
public partial class ShellWindow
{
    const double HitMargin = 6;   // pixels of slack around the shape, in device units
    readonly DispatcherTimer _hitTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
    bool _passThrough;

    void WireHitArea()
    {
        _hitTimer.Tick += (_, _) => UpdateHitArea();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _hitTimer.Start();
            else _hitTimer.Stop();
        };
    }

    void UpdateHitArea()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        // a drag on the grip, or a click in progress, keeps the window until it ends
        var inside = Mouse.Captured is not null || Mouse.LeftButton == MouseButtonState.Pressed && !_passThrough || PointerOverShape();
        SetPassThrough(!inside);
    }

    bool PointerOverShape()
    {
        if (!Native.GetCursorPos(out var p)) return true;
        var point = new Point(p.X, p.Y);
        if (ScreenRect(Shell).Contains(point)) return true;
        return CheckInCard.Visibility == Visibility.Visible && CheckInCard.Opacity > 0.05
            && ScreenRect(CheckInCard).Contains(point);
    }

    static Rect ScreenRect(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return Rect.Empty;
        try
        {
            var a = element.PointToScreen(new Point(0, 0));
            var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            var rect = new Rect(a, b);
            rect.Inflate(HitMargin, HitMargin);
            return rect;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;   // not connected to a screen yet
        }
    }

    void SetPassThrough(bool on)
    {
        if (on == _passThrough) return;
        _passThrough = on;
        if (on) Native.AddExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
        else Native.RemoveExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
    }
}
