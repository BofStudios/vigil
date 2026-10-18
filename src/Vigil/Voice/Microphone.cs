using NAudio.Wave;

namespace Vigil.Voice;

/// <summary>The microphone, open for as long as voice is on, kept in memory only.
///
/// It holds the last few seconds so that "Hey Vigil, open Notepad" said in one
/// breath is not cut off at the comma, and it decides when someone has finished
/// speaking by listening for the silence after them. Nothing it hears is written
/// to disk, and nothing leaves the machine until the wake phrase has been heard.</summary>
public sealed class Microphone : IDisposable
{
    public const int Rate = 16000;
    const int FrameMs = 30;
    static readonly TimeSpan Keep = TimeSpan.FromSeconds(20);

    readonly object _lock = new();
    readonly LinkedList<(DateTime At, byte[] Data)> _ring = new();
    WaveInEvent? _input;
    Capture? _capture;
    double _floor = 120;

    /// <summary>0..1, roughly how loud the room is right now. For the listening animation.</summary>
    public event Action<float>? Level;
    public event Action<string>? Failed;

    public bool Running => _input is not null;

    /// <summary>Listens for three claps on the same audio, while nothing else is being captured.</summary>
    public ClapDetector Claps { get; } = new();

    public static bool Available => WaveInEvent.DeviceCount > 0;

    public void Start()
    {
        if (_input is not null) return;
        if (!Available) throw new InvalidOperationException("No microphone was found.");
        var input = new WaveInEvent
        {
            DeviceNumber = 0,   // WAVE_MAPPER follows the Windows default device
            WaveFormat = new WaveFormat(Rate, 16, 1),
            BufferMilliseconds = FrameMs,
            NumberOfBuffers = 4,
        };
        input.DataAvailable += OnData;
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Failed?.Invoke("The microphone stopped: " + e.Exception.Message);
        };
        input.StartRecording();
        _input = input;
    }

    public void Stop()
    {
        var input = _input;
        _input = null;
        if (input is null) return;
        try { input.StopRecording(); } catch (Exception) { }
        input.Dispose();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);
        var now = DateTime.UtcNow;
        var rms = Rms(data);

        Capture? capture;
        lock (_lock)
        {
            _ring.AddLast((now, data));
            while (_ring.First is { } first && now - first.Value.At > Keep)
                _ring.RemoveFirst();
            capture = _capture;
            // the floor drifts toward the room's quiet level, never up into speech
            if (capture is null || !capture.Speaking)
                _floor = rms < _floor ? _floor * 0.9 + rms * 0.1 : _floor * 0.995 + Math.Min(rms, _floor * 2) * 0.005;
            _floor = Math.Clamp(_floor, 40, 3000);
        }

        Level?.Invoke((float)Math.Clamp(Math.Log10(1 + rms) / 4.0, 0, 1));
        if (capture is null) Claps.Feed(data);
        capture?.Feed(data, rms, Threshold);
    }

    double Threshold => Math.Max(_floor * 3.2, 420);

    static double Rms(byte[] data)
    {
        double sum = 0;
        var count = data.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sample = (short)(data[2 * i] | (data[2 * i + 1] << 8));
            sum += sample * (double)sample;
        }
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    /// <summary>Listen until the person has said something and stopped.
    /// <paramref name="since"/> pulls in audio already heard (the words that came with
    /// the wake phrase). Returns a WAV, or null when nobody spoke.</summary>
    public async Task<byte[]?> ListenAsync(TimeSpan waitForSpeech, DateTime? since, bool alreadySpoke, CancellationToken ct)
    {
        Start();
        var capture = new Capture(waitForSpeech, alreadySpoke);
        lock (_lock)
        {
            if (since is { } from)
                foreach (var (at, data) in _ring)
                    if (at >= from) capture.Preroll(data);
            _capture = capture;
        }
        try
        {
            await using (ct.Register(() => capture.Done.TrySetCanceled()))
                await capture.Done.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                if (_capture == capture) _capture = null;
        }
        return capture.HeardSpeech ? capture.ToWav() : null;
    }

    public void Dispose() => Stop();

    /// <summary>One utterance being captured: waiting for speech, then for the silence after it.</summary>
    sealed class Capture(TimeSpan waitForSpeech, bool alreadySpoke)
    {
        const int SpeechFrames = 4;        // ~120 ms above the threshold counts as speaking
        static readonly TimeSpan EndSilence = TimeSpan.FromMilliseconds(950);
        static readonly TimeSpan Longest = TimeSpan.FromSeconds(30);

        readonly MemoryStream _audio = new();
        readonly DateTime _started = DateTime.UtcNow;
        DateTime _lastVoice = DateTime.UtcNow;
        int _loudFrames;

        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Speaking { get; private set; } = alreadySpoke;
        public bool HeardSpeech { get; private set; } = alreadySpoke;

        public void Preroll(byte[] data) => _audio.Write(data);

        public void Feed(byte[] data, double rms, double threshold)
        {
            if (Done.Task.IsCompleted) return;
            _audio.Write(data);
            var now = DateTime.UtcNow;

            if (rms > threshold)
            {
                if (++_loudFrames >= SpeechFrames)
                {
                    Speaking = true;
                    HeardSpeech = true;
                }
                _lastVoice = now;
            }
            else if (rms < threshold * 0.75)
                _loudFrames = 0;

            if (!HeardSpeech && now - _started > waitForSpeech)
                Done.TrySetResult(false);
            else if (Speaking && now - _lastVoice > EndSilence)
                Done.TrySetResult(true);
            else if (now - _started > Longest)
                Done.TrySetResult(true);
        }

        public byte[] ToWav()
        {
            var pcm = _audio.ToArray();
            using var output = new MemoryStream();
            using (var writer = new WaveFileWriter(new IgnoreDispose(output), new WaveFormat(Rate, 16, 1)))
                writer.Write(pcm, 0, pcm.Length);
            return output.ToArray();
        }
    }

    /// <summary>WaveFileWriter closes its stream; this one shrugs.</summary>
    sealed class IgnoreDispose(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}
