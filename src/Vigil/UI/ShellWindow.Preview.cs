using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vigil.Core;
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
        if (Environment.GetEnvironmentVariable("VIGIL_PREVIEW_DEMO") == "1") StageDemo();

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

        // the talk pill, with a made-up voice: a few words, a pause, a few more
        var pill = new TalkPill(this);
        var levels = new[] { 0.5f, 0.82f, 0.9f, 0.7f, 0.86f, 0.55f, 0.5f, 0.78f, 0.92f, 0.88f, 0.6f, 0.84f, 0.9f, 0.75f, 0.83f, 0.6f };
        ShotOf(pill.RenderFrame(levels, "Listening  ·  let go to send", false), folder, "8-talk-pill");
        ShotOf(pill.RenderFrame(levels, "Understanding…", true), folder, "8b-talk-pill-hearing");
        pill.Close();
    }

    static void ShotOf(FrameworkElement element, string folder, string name)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = element.DesiredSize;
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x33, 0x40), Color.FromRgb(0x5A, 0x4E, 0x46), 35), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(size));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    /// <summary>VIGIL_PREVIEW_DEMO=1: a made-up day in a throwaway Vigil Mind, so pictures
    /// for the README or an ad never show the real person's notes, journal or apps.</summary>
    void StageDemo()
    {
        var root = Path.Combine(Path.GetTempPath(), "vigil-demo-mind");
        try { Directory.Delete(root, true); } catch (IOException) { }
        Mind.UseRoot(root);
        Mind.Ensure();
        _config.UserName = "";
        _config.Awareness = true;

        Mind.WriteNote("Ideas", "A morning briefing: weather, calendar and the three things that matter today.\n\nVigil could read the news while the coffee is brewing.", append: false);
        Mind.WriteNote("Pitch deck", "Problem, the demo, then the numbers. Keep it to ten slides.\nOpen with the clap.", append: false);
        Mind.WriteNote("Groceries", "Coffee beans, oat milk, eggs, lemons.", append: false);

        Mind.WriteNote("Launch checklist", "- [x] Record the demo\n- [x] Write the README\n- [ ] Post the carousel\n- [ ] Answer the first issues", append: false);
        Mind.WriteNote("Launch checklist", "- [ ] Demo for the investors - Friday, 3 pm", append: true);

        var day = DateTime.Now.Date;
        var journal = new[]
        {
            "- 09:12  **asked**  What was I doing yesterday before I stopped?",
            "- 09:12  **answered**  You were halfway through the pitch deck - the numbers slide. Shall I open it?",
            "- 10:40  **did**  notes: write Ideas",
            "- 13:05  **checked in**  Three hours in Figma. Want me to note where you got to before lunch?",
            "- 14:22  **asked**  Clean up my Downloads folder",
            "- 14:23  **did**  sorted 214 files into Images, Documents and Installers",
            "- 16:48  **asked**  Remember that the demo is on Friday at 3 pm",
            "- 16:48  **did**  notes: append Launch checklist",
        };
        File.WriteAllText(Path.Combine(root, "Journal", day.ToString("yyyy-MM-dd") + ".md"),
                          "# " + day.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + "\n\n" + string.Join("\n", journal) + "\n");

        Activity.Stage(new (string, double)[]
        {
            ("Visual Studio Code", 3 * 3600 + 12 * 60), ("Figma", 1 * 3600 + 48 * 60), ("Google Chrome", 65 * 60),
            ("Spotify", 52 * 60), ("Discord", 24 * 60), ("File Explorer", 9 * 60),
        }, "Visual Studio Code", TimeSpan.FromMinutes(72));
        _checkInMessage = "An hour and twelve minutes in VS Code since your break. Want me to write down where you got to, so you can pick it up tomorrow?";
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
