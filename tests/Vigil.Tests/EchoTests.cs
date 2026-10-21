using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>Talk mode must not answer its own voice. These are the cases that matter: what comes
/// back through the microphone is Vigil's own words; what the person says is not, even when it
/// shares a few words with the question.</summary>
public class EchoTests
{
    const string Said = "I can write the script and schedule it for you. Shall I set it for 7 am, every day?";

    [Theory]
    [InlineData("Shall I set it for 7 am, every day?")]
    [InlineData("schedule it for you. Shall I set it for 7 am")]
    [InlineData("I can write the script and schedule it")]
    public void Its_own_voice_coming_back_is_an_echo(string heard)
    {
        Assert.True(SpeechText.IsEcho(heard, Said), heard);
    }

    [Theory]
    [InlineData("Yes, set it for 7 am")]
    [InlineData("Yes please")]
    [InlineData("No, make it 8 am")]
    [InlineData("Stop")]
    [InlineData("Open Spotify")]
    [InlineData("What does schedule it mean in this case")]
    public void A_real_reply_is_not_an_echo(string heard) => Assert.False(SpeechText.IsEcho(heard, Said), heard);

    [Fact]
    public void Nothing_said_means_nothing_to_echo() => Assert.False(SpeechText.IsEcho("Shall I set it for 7 am", ""));

    [Fact]
    public void Silence_is_not_an_echo() => Assert.False(SpeechText.IsEcho("", Said));
}
