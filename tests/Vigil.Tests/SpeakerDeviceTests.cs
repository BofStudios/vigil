using NAudio.Wave;
using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>The real speaker, on the real sound card, recorded back through Windows' loopback:
/// when Vigil says it has finished, the sound card must have gone quiet too. Plays out loud,
/// so it runs only with VIGIL_AUDIO=1.</summary>
public class SpeakerDeviceTests
{
    static bool On => Environment.GetEnvironmentVariable("VIGIL_AUDIO") == "1";

    [Fact]
    public async Task When_it_stops_speaking_the_sound_card_is_quiet()
    {
        if (!On) return;
        var config = new Config { TtsEngine = "windows" };
        using var speaker = new Speaker(config);

        var frames = new List<(DateTime At, float Peak)>();
        using var loop = new WasapiLoopbackCapture();
        loop.DataAvailable += (_, e) =>
        {
            float peak = 0;
            var fmt = loop.WaveFormat;
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32)
                for (var i = 0; i + 4 <= e.BytesRecorded; i += 4)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
            lock (frames) frames.Add((DateTime.UtcNow, peak));
        };
        var pulls = new List<(DateTime At, float Level)>();
        speaker.Level += level => { lock (pulls) pulls.Add((DateTime.UtcNow, level)); };
        loop.StartRecording();
        await Task.Delay(300);

        speaker.Say("Testing the voice. One, two, three.", "en");
        await speaker.WhenDoneAsync();
        var ended = DateTime.UtcNow;
        await Task.Delay(2500);
        loop.StopRecording();

        List<(DateTime At, float Peak)> copy;
        lock (frames) copy = frames.ToList();
        var during = copy.Where(f => f.At < ended).Select(f => f.Peak).DefaultIfEmpty(0).Max();
        var after = copy.Where(f => f.At > ended + TimeSpan.FromMilliseconds(600)).Select(f => f.Peak).DefaultIfEmpty(0).Max();
        var report = $"loopback format {loop.WaveFormat}; peak while speaking {during:0.000}, peak from 0.6 s after it said it was done {after:0.000}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-speaker-device.txt"), report + Environment.NewLine +
            string.Join(Environment.NewLine, copy.Select(f => (f.At - ended).TotalMilliseconds.ToString("0") + "\t" + f.Peak.ToString("0.000"))));
        List<(DateTime At, float Level)> pc;
        lock (pulls) pc = pulls.ToList();
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "vigil-speaker-pulls.txt"),
            pc.Select(p => (p.At - ended).TotalMilliseconds.ToString("0") + "\t" + p.Level.ToString("0.000")));
        Assert.True(during > 0.01, "nothing was heard at all - " + report);
        Assert.True(after < during * 0.1, report);
    }
}
