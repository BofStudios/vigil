using System.IO;
using System.Text;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>Talks to the real model on the real key. Off unless VIGIL_LIVE=1, because it
/// spends the person's quota and needs the network.</summary>
[Collection("mind")]
public class LiveTests : IDisposable
{
    // a throwaway Vigil Mind: tests must never write into the person's real journal
    readonly string _root = Path.Combine(Path.GetTempPath(), "vigil-live-mind-" + Guid.NewGuid().ToString("N")[..8]);

    public LiveTests() => Mind.UseRoot(_root);

    public void Dispose()
    {
        Mind.UseRoot("");
        Activity.Stage(Array.Empty<(string, double)>(), "", TimeSpan.Zero, staged: false);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    sealed class Host : IAgentHost
    {
        public readonly StringBuilder Streamed = new();
        public readonly List<string> Tools = new();
        public readonly List<string> Summaries = new();
        public readonly List<string> Errors = new();
        public int Asked;

        public void StreamText(string chunk) { lock (Streamed) Streamed.Append(chunk); }
        public void EndStream() { }
        public void ToolStarted(string tool, string summary, Risk risk) { Tools.Add(tool); Summaries.Add(summary); }
        public void ToolFinished(string tool, string output, bool ok) { }
        public void Note(string text) { }
        public void Warn(string text) => Errors.Add(text);
        public void Error(string text) => Errors.Add(text);
        public void PlanChanged(IReadOnlyList<PlanStep> steps) { }
        public string? AskUser(string question, IReadOnlyList<string> choices, CancellationToken ct) => null;
        public Answer Confirm(GuardAction action) { Asked++; return Answer.No; }
        public IDisposable Driving() => new Nothing();
        public IDisposable OutOfShot() => new Nothing();
        sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    static bool Live => Environment.GetEnvironmentVariable("VIGIL_LIVE") == "1";

    [Fact]
    public async Task It_uses_a_tool_and_answers_without_inventing_things_to_offer()
    {
        if (!Live) return;
        var config = Config.Load();
        var host = new Host();
        var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);

        var answer = await agent.RunAsync("What time is it right now? Check with your tool.");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-answer.txt"), answer);
        Assert.Empty(host.Errors);
        Assert.Contains("system", host.Tools);
        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.DoesNotMatch("(?i)alarm|close (google )?chrome", answer);   // what it said before: nobody asked for that
        Assert.DoesNotMatch("(?i)^\\W*(hey|hi|hello)\\b", answer);
    }

    [Fact]
    public async Task Hello_is_answered_like_a_person_not_with_a_report_on_the_apps()
    {
        if (!Live) return;
        // what it saw when it answered "Hello?" with League of Legends and Chrome times
        Activity.Stage(new (string, double)[] { ("League of Legends", 13 * 60), ("Google Chrome", 40) }, "Google Chrome", TimeSpan.FromSeconds(40));
        var config = Config.Load();
        var host = new Host();
        var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);

        var answer = await agent.RunAsync("Hello?", spokenLanguage: "english");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-hello.txt"), answer);
        Assert.DoesNotMatch("(?i)league|chrome|minutes", answer);
        Assert.DoesNotMatch("(?i)hey vigil|^\\W*vigil\\b", answer);
        Assert.True(answer.Length < 300, answer);
    }

    [Fact]
    public async Task A_spoken_turkish_question_gets_a_short_turkish_answer()
    {
        if (!Live) return;
        var config = Config.Load();
        var host = new Host();
        var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);

        config.ReplyLanguage = "auto";
        var answer = await agent.RunAsync("Merhaba Vigil, nasılsın?", spokenLanguage: "turkish");

        Assert.Empty(host.Errors);
        Assert.True(answer.Length < 400, answer);
        Assert.Equal("tr", Vigil.Voice.SpeechText.GuessLanguage(answer, "?"));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-answer-tr.txt"), answer);
    }

    [Fact]
    public async Task Spoken_turkish_gets_an_english_answer_that_offers_the_next_step()
    {
        if (!Live) return;
        var config = Config.Load();
        config.ReplyLanguage = "en";
        var host = new Host();
        var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);

        var answer = await agent.RunAsync("Her sabah haberleri okuyan bir şey yapmak istiyorum", spokenLanguage: "turkish");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-answer-en.txt"), answer);
        Assert.Empty(host.Errors);
        Assert.Equal("en", Vigil.Voice.SpeechText.GuessLanguage(answer, "en"));
        Assert.DoesNotMatch("[ğşıİ]", answer);
        Assert.EndsWith("?", answer.TrimEnd());
    }

    [Fact]
    public async Task A_check_in_is_written_by_the_model_and_asks_something()
    {
        if (!Live) return;
        var config = Config.Load();
        var moment = new CheckIn.Moment("streak", "Visual Studio Code", TimeSpan.FromMinutes(95));

        var text = await new CheckIn(config).WriteAsync(ProviderFactory.Build(config), moment, CancellationToken.None);

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-checkin.txt"), text);
        Assert.NotEqual(CheckIn.Fallback(moment), text);
        Assert.True(text.Length < 300, text);
        Assert.Contains("?", text);
    }

    [Fact]
    public async Task Take_a_note_lands_in_vigil_mind()
    {
        if (!Live) return;
        var root = Path.Combine(Path.GetTempPath(), "vigil-live-note-" + Guid.NewGuid().ToString("N")[..8]);
        Mind.UseRoot(root);
        try
        {
            Mind.Ensure();
            var config = Config.Load();
            var host = new Host();
            var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);

            await agent.RunAsync("Take a note called Demo: the investor demo is on Friday at 3 pm.");

            Assert.Empty(host.Errors);
            Assert.Contains("notes", host.Tools);
            Assert.Contains(Mind.ListNotes(), n => File.ReadAllText(n.Path).Contains("Friday"));
        }
        finally
        {
            Mind.UseRoot(_root);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_half_remembered_song_is_worked_out_and_played()
    {
        if (!Live) return;
        var config = Config.Load();
        config.ApprovalMode = "ask";   // the play is asked about - and declined - so nothing opens during the test
        var host = new Host();
        var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);

        await agent.RunAsync("Madonna'dan prayer şarkısını aç", spokenLanguage: "turkish");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-play.txt"), string.Join("\n", host.Summaries));
        Assert.Contains("play", host.Tools);
        Assert.Contains(host.Summaries, s => s.Contains("Like a Prayer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task YouTube_finds_the_song()
    {
        if (!Live) return;
        var found = await MediaTools.FindAsync("Madonna Like a Prayer", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(11, found!.Value.Id.Length);
        Assert.Contains("Prayer", found.Value.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Whisper_hears_what_windows_says()
    {
        if (!Live) return;
        var config = Config.Load();
        var ears = ProviderFactory.Ears(config)!;
        var speaker = new Vigil.Voice.Speaker(config);
        // synthesise a sentence with the Windows voice, then hand it to Whisper
        var voice = speaker.VoiceFor("en")!;
        using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer { Voice = voice };
        using var stream = await synth.SynthesizeTextToStreamAsync("Open the calculator, please.");
        using var read = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        var heard = await ears.TranscribeAsync(buffer.ToArray(), "", CancellationToken.None);
        Assert.Contains("calculator", heard.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("en", Vigil.Voice.Speaker.Code(heard.Language));
    }
}
