using System.IO;
using NAudio.Wave;
using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>Push to talk: while the key is held a pause is not the end; letting go is. A tap turns
/// it into "until you stop talking". And a blip is not someone speaking.</summary>
public class PushToTalkTests
{
    const double Threshold = 500;
    static readonly byte[] Frame = new byte[960];   // 30 ms of 16 kHz mono, the contents do not matter here

    static void Talk(Microphone.Capture capture, int frames)
    {
        for (var i = 0; i < frames; i++) capture.Feed(Frame, 2000, Threshold);
    }

    static void Quiet(Microphone.Capture capture, TimeSpan forHowLong)
    {
        var until = DateTime.UtcNow + forHowLong;
        while (DateTime.UtcNow < until)
        {
            capture.Feed(Frame, 50, Threshold);
            Thread.Sleep(30);
        }
    }

    [Fact]
    public void Held_it_keeps_listening_through_a_pause_and_ends_when_let_go()
    {
        var capture = new Microphone.Capture(TimeSpan.FromSeconds(7), false, false) { Held = true };
        Talk(capture, 20);
        Quiet(capture, TimeSpan.FromMilliseconds(1300));   // longer than the end-of-speech silence
        Assert.False(capture.Done.Task.IsCompleted);

        capture.Release();
        Assert.True(capture.Done.Task.IsCompleted);
        Assert.True(capture.Heard);
    }

    [Fact]
    public void Held_and_let_go_without_a_word_is_nothing()
    {
        var capture = new Microphone.Capture(TimeSpan.FromSeconds(7), false, false) { Held = true };
        Quiet(capture, TimeSpan.FromMilliseconds(200));
        capture.Release();
        Assert.False(capture.Heard);
    }

    [Fact]
    public void After_a_tap_it_ends_at_the_silence_after_speech()
    {
        var capture = new Microphone.Capture(TimeSpan.FromSeconds(7), false, false) { Held = true };
        capture.Untether();
        Talk(capture, 20);
        Quiet(capture, TimeSpan.FromMilliseconds(1300));
        Assert.True(capture.Done.Task.IsCompleted);
        Assert.True(capture.Heard);
    }

    [Fact]
    public void In_a_conversation_a_blip_is_not_speech()
    {
        var capture = new Microphone.Capture(TimeSpan.FromSeconds(7), false, true);
        Talk(capture, 8);   // ~240 ms: past the "speaking" mark, short of a word
        Quiet(capture, TimeSpan.FromMilliseconds(1100));
        Assert.True(capture.Done.Task.IsCompleted);
        Assert.False(capture.Heard);
    }

    [Fact]
    public void ElevenLabs_pcm_becomes_a_wav_that_plays()
    {
        var pcm = new byte[24000 * 2];   // one second of silence at 24 kHz, 16-bit
        var wav = ElevenLabs.Wav(pcm, 24000);
        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.Equal(24000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(1.0, reader.TotalTime.TotalSeconds, 2);
    }
}

[Collection("mind")]
public class VoiceDefaultsTests : IDisposable
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "vigil-config-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _before = Paths.Home;

    public VoiceDefaultsTests()
    {
        Paths.UseHome(_home);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        Paths.UseHome(_before);
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    [Fact]
    public void A_new_install_listens_only_while_the_key_is_held()
    {
        var c = Config.Load();
        Assert.False(c.WakeWord);
        Assert.False(c.ClapToWake);
        Assert.Equal(0, c.FollowUpSeconds);
    }

    [Fact]
    public void An_older_file_that_listened_all_the_time_is_brought_over_once()
    {
        File.WriteAllText(Paths.ConfigFile, "{ \"wake_word\": true, \"clap_to_wake\": true, \"follow_up_seconds\": 6 }");
        var c = Config.Load();
        Assert.False(c.WakeWord);
        Assert.False(c.ClapToWake);
        Assert.Equal(0, c.FollowUpSeconds);
        Assert.True(c.Migrated);
        c.Save();

        // switched back on after that, it stays on
        c.WakeWord = true;
        c.Save();
        var again = Config.Load();
        Assert.True(again.WakeWord);
        Assert.False(again.Migrated);
    }
}
