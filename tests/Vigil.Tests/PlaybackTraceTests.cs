using NAudio.Wave;
using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>The app's own voice and decode path, pulled the way the sound card pulls it (40 ms at
/// a time): every sample the queue hands out after the sentences have ended must be silence.</summary>
public class PlaybackTraceTests
{
    [Fact]
    public async Task After_the_last_sentence_only_silence_comes_out()
    {
        var config = new Config { TtsEngine = "windows" };
        using var speaker = new Speaker(config);
        if (Speaker.WindowsVoices().Count == 0) return;

        var queue = new Speaker.ClipQueue(WaveFormat.CreateIeeeFloatWaveFormat(24000, 1));
        double total = 0;
        foreach (var sentence in new[] { "Testing the voice.", "One, two, three." })
        {
            var wav = await speaker.SynthesiseAsync(sentence, "en", CancellationToken.None);
            using (var r = new WaveFileReader(new MemoryStream(wav))) total += r.TotalTime.TotalSeconds;
            queue.Add(Speaker.Decode(wav)!);
        }

        var block = new float[960];   // 40 ms at 24 kHz
        var drained = false;
        double drainedAt = -1;
        queue.Drained += () => drained = true;
        var loudAfter = 0;
        var pulled = 0.0;
        var log = new List<string>();
        while (pulled < total + 4)
        {
            queue.Read(block, 0, block.Length);
            var peak = block.Max(Math.Abs);
            if (drained && drainedAt < 0) drainedAt = pulled;
            if (pulled > total + 0.3 && peak > 0.003f) loudAfter++;
            log.Add(pulled.ToString("0.00") + "\t" + peak.ToString("0.000") + (drained ? "\tdrained" : ""));
            pulled += 0.04;
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "vigil-playback-trace.txt"), log);
        Assert.True(drainedAt >= total - 0.2, $"said it was done at {drainedAt:0.00}s of {total:0.00}s");
        Assert.True(loudAfter == 0, $"{loudAfter} blocks of sound after the {total:0.00}s of speech");
    }
}
