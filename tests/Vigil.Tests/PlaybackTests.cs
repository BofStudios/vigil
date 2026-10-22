using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vigil.Voice;
using WinSpeech = Windows.Media.SpeechSynthesis;

namespace Vigil.Tests;

/// <summary>The playback chain without a sound card: a sentence from the Windows voice goes through
/// the same reader, resampler and queue Vigil plays from, and is pulled the way the device pulls it.
/// What comes out must be the sentence once - not longer, not looping.</summary>
public class PlaybackTests
{
    const int Rate = 24000;

    static async Task<byte[]?> Windows(string text)
    {
        var voice = WinSpeech.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("en"));
        if (voice is null) return null;
        using var synth = new WinSpeech.SpeechSynthesizer { Voice = voice };
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        using var read = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    static ISampleProvider Clip(byte[] wav)
    {
        ISampleProvider clip = new WaveFileReader(new MemoryStream(wav)).ToSampleProvider();
        if (clip.WaveFormat.Channels == 2) clip = new StereoToMonoSampleProvider(clip);
        if (clip.WaveFormat.SampleRate != Rate) clip = new WdlResamplingSampleProvider(clip, Rate);
        return clip;
    }

    /// <summary>Pulls in device-sized blocks until the queue says it has drained (or a hard stop).</summary>
    static (double Sound, double Pulled) Drain(Speaker.ClipQueue queue, double atMostSeconds)
    {
        var drained = false;
        queue.Drained += () => drained = true;
        var block = new float[Rate * 120 / 1000];   // the 120 ms the device asks for
        long loud = 0, pulled = 0;
        while (!drained && pulled < Rate * atMostSeconds)
        {
            queue.Read(block, 0, block.Length);
            pulled += block.Length;
            loud += block.Count(s => Math.Abs(s) > 0.003f);
        }
        return (loud / (double)Rate, pulled / (double)Rate);
    }

    static double Seconds(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        return reader.TotalTime.TotalSeconds;
    }

    [Fact]
    public async Task One_sentence_plays_once_and_then_stops()
    {
        var wav = await Windows("Hey, good to see you. Would you like me to pull up your schedule for today?");
        if (wav is null) return;   // no Windows voice on this machine
        var queue = new Speaker.ClipQueue(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
        queue.Add(Clip(wav));

        var (sound, pulled) = Drain(queue, 30);

        var length = Seconds(wav);
        Assert.True(pulled < length + 2, $"still playing after {pulled:0.0}s for a {length:0.0}s sentence");
        Assert.True(sound <= length + 0.1, $"{sound:0.0}s of sound from a {length:0.0}s sentence");
    }

    [Fact]
    public async Task Sentences_one_after_another_play_each_once()
    {
        var parts = new[] { "Hey, good to see you.", "Would you like me to pull up your schedule for today?" };
        var wavs = new List<byte[]>();
        foreach (var part in parts)
            if (await Windows(part) is { } w) wavs.Add(w);
        if (wavs.Count == 0) return;
        var queue = new Speaker.ClipQueue(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
        foreach (var w in wavs) queue.Add(Clip(w));

        var (_, pulled) = Drain(queue, 30);

        var length = wavs.Sum(Seconds);
        Assert.True(pulled < length + 2, $"still playing after {pulled:0.0}s for {length:0.0}s of speech");
    }
}
