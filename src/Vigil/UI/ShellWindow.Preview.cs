using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vigil.Core.Security;
using Vigil.Core.Tools;
using Vigil.Voice;

namespace Vigil.UI;

/// <summary>Draws every state of the window into PNG files without putting anything
/// on the screen: `Vigil.exe --preview <folder>`. For checking the look of a change
/// while something else (a game, a call) owns the display.</summary>
public partial class ShellWindow
{
    bool _preview;

    public void RenderPreview(string folder)
    {
        _preview = true;
        Directory.CreateDirectory(folder);
        _watch.Stop();

        Snap(Shape.Pill);
        Shot(folder, "1-pill");

        Snap(Shape.Bar);
        Shot(folder, "2-bar");

        Input.Text = "Open Spotify and put on something calm";
        Placeholder.Visibility = Visibility.Collapsed;
        Shot(folder, "3-bar-typing");
        Input.Text = "";
        Placeholder.Visibility = Visibility.Visible;

        ShowVoiceState(VoiceState.Listening);
        _micLevel = 0.7f;
        for (var i = 0; i < 14; i++) AnimateLevels();
        Shot(folder, "4-listening");
        ShowVoiceState(VoiceState.Speaking);
        _speakLevel = 0.55f;
        for (var i = 0; i < 14; i++) AnimateLevels();
        Shot(folder, "4b-speaking");
        ShowVoiceState(VoiceState.Idle);
        _alive = 0;
        AnimateLevels();
        _levelTimer.Stop();

        Snap(Shape.Panel);
        AddUser("Clean up my downloads folder", spoken: false);
        ShowThinking();
        Shot(folder, "5a-thinking");
        Thread.Children.Clear();
        _thinking = null;
        AddUser("Open Spotify and put on something calm", spoken: true);
        var open = new ToolRow(this, "open_app", "open: Spotify", Risk.Moderate);
        Thread.Children.Add(open.Root);
        open.Finish("Spotify opened.", true);
        var keys = new ToolRow(this, "press_keys", "key: playpause", Risk.Moderate);
        Thread.Children.Add(keys.Root);
        keys.Finish("playpause pressed.", true);
        var running = new ToolRow(this, "browser_open", "open: open.spotify.com/playlist/peaceful-piano", Risk.Moderate);
        Thread.Children.Add(running.Root);
        var box = NewAnswerBox();
        box.Document = Markdown.Render(
            "Spotify is open and **Peaceful Piano** is playing at 40% - quiet enough for late work.\n\n" +
            "Shall I queue the same playlist for 8 am tomorrow?",
            B("TextBrush"), B("TextDim"), B("Sunken"), (FontFamily)FindResource("Sans"), (FontFamily)FindResource("Mono"));
        Thread.Children.Add(box);
        DrawPlan(new List<PlanStep>
        {
            new("Open Spotify", "done"),
            new("Find a calm playlist", "done"),
            new("Start playing", "doing"),
        });
        RefreshFooter();
        Shot(folder, "5-panel");

        var action = new GuardAction("keyboard_type", "typing: Meeting moved to 3 pm - see you there", new Verdict(Risk.Moderate, "keyboard input"),
                                     "The text goes to whichever window currently has focus.");
        ShowApprovalPreview(action);
        Shot(folder, "6-approval");
        Scrim.Visibility = Visibility.Collapsed;

        ShowSetup();
        Snap(Shape.Panel);
        Shot(folder, "7-setup");
        HideSetup();

        new HomeWindow(this).RenderPages(folder);
    }

    void ShowApprovalPreview(GuardAction action)
    {
        var pending = new TaskCompletionSource<Answer>();
        ShowApproval(action, pending);
        Scrim.BeginAnimation(OpacityProperty, null);
        Scrim.Opacity = 1;
        Scrim.Visibility = Visibility.Visible;
        AllowButton.IsEnabled = DenyButton.IsEnabled = AlwaysButton.IsEnabled = true;
    }

    /// <summary>Jumps straight to a shape, no animation.</summary>
    void Snap(Shape shape)
    {
        _shape = shape;
        var (width, height) = shape switch
        {
            Shape.Pill => (PillW, PillH),
            Shape.Bar => (BarW, BarH),
            _ => (BarW, PanelHeight()),
        };
        foreach (var (element, property) in new (UIElement, DependencyProperty)[]
                 {
                     (Shell, WidthProperty), (Shell, HeightProperty), (PillFace, OpacityProperty),
                     (Body, OpacityProperty), (Panel, OpacityProperty),
                 })
            element.BeginAnimation(property, null);
        Shell.Width = width;
        Shell.Height = height;
        PillFace.Visibility = shape == Shape.Pill ? Visibility.Visible : Visibility.Collapsed;
        PillFace.Opacity = 1;
        Body.Visibility = shape == Shape.Pill ? Visibility.Collapsed : Visibility.Visible;
        Body.Opacity = 1;
        Panel.Visibility = shape == Shape.Panel ? Visibility.Visible : Visibility.Collapsed;
        Panel.Opacity = 1;
    }

    void Shot(string folder, string name)
    {
        var size = new Size(Width, Height);
        Root.Measure(size);
        Root.Arrange(new Rect(size));
        Root.UpdateLayout();
        if (_shape == Shape.Panel)
        {
            Shell.Height = PanelHeight();
            Root.UpdateLayout();
        }
        Reshape();
        Root.UpdateLayout();

        const double scale = 2;
        var content = new RenderTargetBitmap((int)(Width * scale), (int)(Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        content.Render(Root);
        var bitmap = new RenderTargetBitmap((int)(Width * scale), (int)(Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // a desktop-ish backdrop, so the edges and the shadow can be judged
            // VIGIL_PREVIEW_BG=#0B0B0E draws on a flat colour instead, for marketing shots
            var flat = Environment.GetEnvironmentVariable("VIGIL_PREVIEW_BG");
            Brush backdrop = !string.IsNullOrWhiteSpace(flat)
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(flat))
                : new LinearGradientBrush(Color.FromRgb(0x2B, 0x33, 0x40), Color.FromRgb(0x5A, 0x4E, 0x46), 35);
            dc.DrawRectangle(backdrop, null, new Rect(size));
            dc.DrawImage(content, new Rect(size));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
