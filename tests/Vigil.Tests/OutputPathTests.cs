using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>Which way of reaching the sound card leaves it quiet afterwards? The same sentence through
/// waveOut at 24 kHz, waveOut at 48 kHz stereo, and WASAPI shared mode, each recorded back through
/// loopback. Plays out loud: VIGIL_AUDIO=1 only.</summary>
public class OutputPathTests
{
    static bool On => Environment.GetEnvironmentVariable("VIGIL_AUDIO") == "1";

    static async Task<(float During, float After)> Measure(Func<IWavePlayer> make, ISampleProvider source, double seconds)
    {
        var frames = new List<(DateTime At, float Peak)>();
        using var loop = new WasapiLoopbackCapture();
        loop.DataAvailable += (_, e) =>
        {
            float peak = 0;
            for (var i = 0; i + 4 <= e.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
            lock (frames) frames.Add((DateTime.UtcNow, peak));
        };
        loop.StartRecording();
        await Task.Delay(300);
        var player = make();
        player.Init(new SampleToWaveProvider(source));
        player.Play();
        var start = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(seconds + 0.3));
        var ended = DateTime.UtcNow;
        await Task.Delay(2000);
        player.Stop();
        player.Dispose();
        loop.StopRecording();
        lock (frames)
            return (frames.Where(f => f.At > start && f.At < ended).Select(f => f.Peak).DefaultIfEmpty(0).Max(),
                    frames.Where(f => f.At > ended + TimeSpan.FromMilliseconds(500)).Select(f => f.Peak).DefaultIfEmpty(0).Max());
    }

    /// <summary>A clip followed by silence for ever - what Vigil's queue does.</summary>
    sealed class ThenSilence(ISampleProvider clip) : ISampleProvider
    {
        bool _done;
        public WaveFormat WaveFormat => clip.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var read = _done ? 0 : clip.Read(buffer, offset, count);
            if (read == 0) _done = true;
            Array.Clear(buffer, offset + read, count - read);
            return count;
        }
    }

    [Fact]
    public async Task Compare_the_ways_out()
    {
        if (!On) return;
        using var speaker = new Speaker(new Config { TtsEngine = "windows" });
        var wav = await speaker.SynthesiseAsync("Testing the voice. One, two, three.", "en", CancellationToken.None);
        double seconds;
        using (var r = new WaveFileReader(new MemoryStream(wav))) seconds = r.TotalTime.TotalSeconds;

        var results = new List<string>();

        var a = await Measure(() => new WaveOutEvent { DesiredLatency = 160, NumberOfBuffers = 4 },
                              new ThenSilence(Speaker.Decode(wav)!), seconds);
        results.Add($"waveOut 24k mono: during {a.During:0.000} after {a.After:0.000}");

        var b = await Measure(() => new WaveOutEvent { DesiredLatency = 160, NumberOfBuffers = 4 },
                              new ThenSilence(new MonoToStereoSampleProvider(new WdlResamplingSampleProvider(Speaker.Decode(wav)!, 48000))), seconds);
        results.Add($"waveOut 48k stereo: during {b.During:0.000} after {b.After:0.000}");

        var c = await Measure(() => new WasapiOut(AudioClientShareMode.Shared, true, 60),
                              new ThenSilence(Speaker.Decode(wav)!), seconds);
        results.Add($"WASAPI shared 24k mono: during {c.During:0.000} after {c.After:0.000}");

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "vigil-output-paths.txt"), results);
    }
}
