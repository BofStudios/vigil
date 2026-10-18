using System.IO;
using System.Text;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>Talks to the real model on the real key. Off unless VIGIL_LIVE=1, because it
/// spends the person's quota and needs the network.</summary>
public class LiveTests
{
    sealed class Host : IAgentHost
    {
        public readonly StringBuilder Streamed = new();
        public readonly List<string> Tools = new();
        public readonly List<string> Errors = new();
        public int Asked;

        public void StreamText(string chunk) { lock (Streamed) Streamed.Append(chunk); }
        public void EndStream() { }
        public void ToolStarted(string tool, string summary, Risk risk) => Tools.Add(tool);
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
    public async Task It_uses_a_tool_answers_and_closes_with_a_question()
    {
        if (!Live) return;
        var config = Config.Load();
        var host = new Host();
        var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);

        var answer = await agent.RunAsync("What time is it right now? Check with your tool.");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-errors.txt"), string.Join(Environment.NewLine, host.Errors));
        Assert.Empty(host.Errors);
        Assert.Contains("system", host.Tools);
        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.EndsWith("?", answer.TrimEnd().TrimEnd('*', '_', '"', '”', ')'));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-live-answer.txt"), answer);
    }

    [Fact]
    public async Task A_spoken_turkish_question_gets_a_short_turkish_answer_with_a_question()
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
        Assert.EndsWith("?", answer.TrimEnd());
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
