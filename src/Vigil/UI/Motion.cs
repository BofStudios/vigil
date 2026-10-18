using System.Windows;
using System.Windows.Media.Animation;

namespace Vigil.UI;

/// <summary>One curve for everything: quick to leave, slow to arrive, no overshoot.
/// cubic-bezier(.22, .61, .18, 1) - the same one the first Vigil used.</summary>
public sealed class Flow : EasingFunctionBase
{
    const double X1 = .22, Y1 = .61, X2 = .18, Y2 = 1;

    protected override double EaseInCore(double t)
    {
        // EasingMode.EaseIn means "use the curve as written"
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var s = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Bezier(s, X1, X2) - t;
            var slope = Slope(s, X1, X2);
            if (Math.Abs(x) < 1e-5 || Math.Abs(slope) < 1e-6) break;
            s = Math.Clamp(s - x / slope, 0, 1);
        }
        return Bezier(s, Y1, Y2);
    }

    static double Bezier(double s, double p1, double p2) =>
        3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;

    static double Slope(double s, double p1, double p2) =>
        3 * (1 - s) * (1 - s) * p1 + 6 * (1 - s) * s * (p2 - p1) + 3 * s * s * (1 - p2);

    protected override Freezable CreateInstanceCore() => new Flow();

    public static readonly Flow Curve = Make();

    static Flow Make()
    {
        var curve = new Flow { EasingMode = EasingMode.EaseIn };
        curve.Freeze();
        return curve;
    }
}

public static class Motion
{
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    public static void To(this UIElement element, DependencyProperty property, double value, double ms, double delayMs = 0, Action? done = null)
    {
        if (Reduced) ms = Math.Min(ms, 1);
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = Flow.Curve,
            BeginTime = TimeSpan.FromMilliseconds(Reduced ? 0 : delayMs),
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (done is not null) animation.Completed += (_, _) => done();
        element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void Fade(this UIElement element, bool show, double ms = 180, double delayMs = 0)
    {
        if (show)
        {
            element.Visibility = Visibility.Visible;
            element.To(UIElement.OpacityProperty, 1, ms, delayMs);
        }
        else
        {
            element.To(UIElement.OpacityProperty, 0, ms, delayMs, () =>
            {
                if (element.Opacity < 0.01) element.Visibility = Visibility.Collapsed;
            });
        }
    }
}
