using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>Work mode reads a stretch of work from app names and minutes alone: how long in each app, how
/// often the app in front changed, the longest unbroken run, and how long since a break.</summary>
[Collection("mind")]
public class WorkModeTests : IDisposable
{
    public void Dispose() => Activity.StageTimeline(Array.Empty<(string, DateTime, DateTime)>());

    static readonly DateTime T = new(2026, 10, 24, 14, 0, 0);
    static DateTime M(double minutes) => T.AddMinutes(minutes);

    [Fact]
    public void A_scattered_stretch_shows_as_switches_and_short_runs()
    {
        Activity.StageTimeline(new[]
        {
            ("Visual Studio Code", M(0), M(3)),
            ("Google Chrome", M(3), M(4)),
            ("Discord", M(4), M(5)),
            ("Visual Studio Code", M(5), M(6)),
            ("Google Chrome", M(6), M(8)),
            ("Discord", M(8), M(10)),
        });
        var stretch = Activity.Between(M(0), M(10));
        Assert.Equal(5, stretch.Switches);
        Assert.Equal(600, stretch.Active, 0);
        Assert.Equal(180, stretch.LongestRun, 0);
        Assert.Equal("Visual Studio Code", stretch.Apps[0].App);   // 4 minutes, the most
        Assert.Equal(10 * 60, stretch.SinceBreak, 0);
    }

    [Fact]
    public void A_short_look_away_does_not_break_a_run_but_five_minutes_away_is_a_break()
    {
        Activity.StageTimeline(new[]
        {
            ("Visual Studio Code", M(0), M(20)),
            // ten minutes away from the keyboard: a break
            ("Visual Studio Code", M(30), M(50)),
            ("Visual Studio Code", M(50.5), M(70)),   // half a minute's pause: the same run
        });
        var stretch = Activity.Between(M(30), M(70));
        Assert.Equal(0, stretch.Switches);
        Assert.Equal(40 * 60, stretch.SinceBreak, 0);
        Assert.True(stretch.LongestRun >= 39 * 60);
    }

    [Fact]
    public void Only_the_stretch_asked_about_is_counted()
    {
        Activity.StageTimeline(new[] { ("Google Chrome", M(0), M(60)) });
        var stretch = Activity.Between(M(50), M(60));
        Assert.Equal(600, stretch.Active, 0);
    }

    [Fact]
    public void Without_the_model_the_rule_of_thumb_still_says_something_useful()
    {
        var long_ = new Activity.Stretch(new[] { ("Visual Studio Code", 600.0) }, 1, 600, 600, "Visual Studio Code", 90 * 60);
        Assert.Contains("break", WorkMode.Fallback(long_, ""));

        var scattered = new Activity.Stretch(new[] { ("Discord", 200.0), ("Chrome", 200.0) }, 15, 400, 60, "Discord", 20 * 60);
        Assert.Contains("15 app switches", WorkMode.Fallback(scattered, "the Vigil release"));
        Assert.Contains("the Vigil release", WorkMode.Fallback(scattered, "the Vigil release"));
    }

    /// <summary>What it actually says, on the real key, for a scattered stretch and a long one - written to a
    /// report to read. VIGIL_LIVE=1 only.</summary>
    [Fact]
    public async Task What_work_mode_says_on_the_real_model()
    {
        if (Environment.GetEnvironmentVariable("VIGIL_LIVE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "vigil-work-mind");
        Mind.UseRoot(root);
        try
        {
            var report = new System.Text.StringBuilder();
            var now = DateTime.Now;
            var config = Config.Load();
            var provider = Vigil.Core.Providers.ProviderFactory.Build(config);
            config.DoNotDisturb = false;

            // ten scattered minutes
            Activity.StageTimeline(Enumerable.Range(0, 20).Select(i =>
                (new[] { "Visual Studio Code", "Google Chrome", "Discord", "WhatsApp" }[i % 4], now.AddMinutes(-10 + i * 0.5), now.AddMinutes(-10 + i * 0.5 + 0.5))));
            var work = new WorkMode(config);
            work.Begin("finishing the Vigil release", now.AddMinutes(-10));
            report.AppendLine("scattered: " + await work.AdviseAsync(provider, CancellationToken.None));

            // ninety focused minutes without a break
            Activity.StageTimeline(new[] { ("Visual Studio Code", now.AddMinutes(-90), now) });
            work = new WorkMode(config);
            work.Begin("finishing the Vigil release", now.AddMinutes(-90));
            report.AppendLine("long: " + await work.AdviseAsync(provider, CancellationToken.None));

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-work.txt"), report.ToString());
        }
        finally
        {
            Mind.UseRoot("");
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("work mode")]
    [InlineData("Work mode on")]
    [InlineData("start work mode")]
    [InlineData("çalışma modunu aç")]
    [InlineData("Çalışma modu.")]
    public void Saying_work_mode_starts_it(string said) => Assert.True(SpeechText.StartsWork(said));

    [Theory]
    [InlineData("stop work mode")]
    [InlineData("work mode off")]
    [InlineData("çalışma modunu kapat")]
    public void Saying_stop_ends_it(string said) => Assert.True(SpeechText.EndsWork(said));

    [Theory]
    [InlineData("what is work mode?")]
    [InlineData("open my work folder")]
    public void A_question_about_it_is_just_a_question(string said)
    {
        Assert.False(SpeechText.StartsWork(said));
        Assert.False(SpeechText.EndsWork(said));
    }
}
