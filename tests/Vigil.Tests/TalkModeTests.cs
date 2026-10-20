using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

public class TalkModeTests
{
    [Theory]
    [InlineData("That's all.")]
    [InlineData("that is all")]
    [InlineData("Goodbye!")]
    [InlineData("bye bye")]
    [InlineData("Talk mode off")]
    [InlineData("We're done.")]
    [InlineData("bu kadar")]
    public void These_end_a_conversation(string said) => Assert.True(SpeechText.EndsTalk(said));

    [Theory]
    [InlineData("That's all I wanted to ask, but can you also open Spotify")]
    [InlineData("Say goodbye to my neighbour in Turkish")]
    [InlineData("Open the calculator")]
    [InlineData("What does bye mean in French?")]
    public void A_longer_request_is_not_an_ending(string said) => Assert.False(SpeechText.EndsTalk(said));

    [Theory]
    [InlineData("Let's talk.")]
    [InlineData("lets chat")]
    [InlineData("Talk mode")]
    [InlineData("start conversation mode")]
    [InlineData("konuşalım")]
    public void These_start_one(string said) => Assert.True(SpeechText.StartsTalk(said));

    [Theory]
    [InlineData("Let's talk about my taxes and open the folder")]
    [InlineData("How do I turn talk mode off in the settings")]
    [InlineData("Open Spotify")]
    public void A_request_that_mentions_talking_does_not_start_it(string said) => Assert.False(SpeechText.StartsTalk(said));
}

[Collection("mind")]
public class LocalVoiceTests : IDisposable
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "vigil-voice-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _before = Paths.Home;

    public LocalVoiceTests()
    {
        Paths.UseHome(_home);
        Directory.CreateDirectory(PiperVoice.Folder);
    }

    public void Dispose()
    {
        Paths.UseHome(_before);
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    [Fact]
    public void Nothing_is_installed_until_the_files_are_there()
    {
        Assert.Null(PiperVoice.Exe());
        Assert.Null(PiperVoice.Model(""));
        Assert.False(PiperVoice.Installed(""));
    }

    [Fact]
    public void A_voice_needs_its_json_beside_it_and_is_found_in_subfolders()
    {
        var folder = Path.Combine(PiperVoice.Folder, "piper");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "piper.exe"), "");
        File.WriteAllText(Path.Combine(PiperVoice.Folder, "en_GB-alan-medium.onnx"), "");
        Assert.Null(PiperVoice.Model(""));   // no .onnx.json yet: not usable

        File.WriteAllText(Path.Combine(PiperVoice.Folder, "en_GB-alan-medium.onnx.json"), "{}");
        File.WriteAllText(Path.Combine(PiperVoice.Folder, "en_US-ryan-high.onnx"), "");
        File.WriteAllText(Path.Combine(PiperVoice.Folder, "en_US-ryan-high.onnx.json"), "{}");

        Assert.EndsWith("piper.exe", PiperVoice.Exe());
        Assert.Contains("alan", PiperVoice.Model("")!);
        Assert.Contains("ryan", PiperVoice.Model("ryan")!);
        Assert.Contains("alan", PiperVoice.Model("nobody-by-that-name")!);   // an unknown name falls back, it does not break
        Assert.True(PiperVoice.Installed(""));
    }

    [Theory]
    [InlineData("en_GB-alan-medium.onnx", "en")]
    [InlineData(@"C:\x\tr_TR-dfki-medium.onnx", "tr")]
    public void A_voice_speaks_the_language_in_its_name(string file, string language) =>
        Assert.Equal(language, PiperVoice.LanguageOf(file));
}
