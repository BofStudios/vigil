namespace Vigil.Voice;

/// <summary>Three claps wake Vigil.
///
/// A clap is a sound that starts all at once and is over in about a tenth of a
/// second. Speech, music and a door closing all carry on longer, so anything still
/// loud 150 ms after it began is not counted. On top of that the claps have to come
/// as a rhythm - exactly three, evenly spaced - which is something typing, talking
/// and a dropped cup almost never do. It works on the audio that is already in
/// memory for the wake phrase; nothing is recorded or sent for it.</summary>
public sealed class ClapDetector
{
    const int Rate = Microphone.Rate;
    const int Window = Rate / 100;               // 10 ms
    const long MinGapMs = 130, MaxGapMs = 900;   // between one clap and the next
    const double MaxGapRatio = 2.3;              // and roughly even
    const long QuietAfterMs = 550;               // a fourth clap means it was not a summons
    const long QuietBeforeMs = 450;              // nothing sustained just before the first

    double _floor = 150;
    double _previous;
    int _sinceOnset = -1;
    double _onsetPeak;
    long _onsetAt;
    long _clock;
    int _loudRun;
    long _lastSustained = long.MinValue / 2;
    long _fireAt = -1;
    readonly List<long> _claps = new();

    /// <summary>1 is the default; higher hears quieter claps, lower needs louder ones.</summary>
    public double Sensitivity { get; set; } = 1.0;

    public event Action? TripleClap;

    /// <summary>16-bit mono PCM at 16 kHz, in any chunk size.</summary>
    public void Feed(ReadOnlySpan<byte> pcm)
    {
        var samples = pcm.Length / 2;
        for (var start = 0; start + Window <= samples; start += Window)
        {
            double sum = 0;
            var peak = 0;
            for (var i = start; i < start + Window; i++)
            {
                int sample = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
                sum += sample * (double)sample;
                var magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }
            Step(Math.Sqrt(sum / Window), peak);
        }
    }

    void Step(double rms, int peak)
    {
        _clock += 10;

        // sustained loudness - talking, music - rules out a summons around it
        if (rms > _floor * 4) { if (++_loudRun > 22) _lastSustained = _clock; }
        else _loudRun = 0;

        if (_sinceOnset >= 0)
        {
            _sinceOnset++;
            if (_sinceOnset <= 2) _onsetPeak = Math.Max(_onsetPeak, rms);
            if (rms < _onsetPeak * 0.22)
            {
                Register(_onsetAt);          // over almost as soon as it began: a clap
                _sinceOnset = -1;
            }
            else if (_sinceOnset >= 15)
            {
                _sinceOnset = -1;            // still going after 150 ms: not a clap
                _lastSustained = _clock;
                _claps.Clear();
                _fireAt = -1;
            }
        }
        else
        {
            var trigger = Math.Max(_floor * 10, 1000) / Math.Clamp(Sensitivity, 0.4, 2.5);
            if (rms > trigger && rms > _previous * 3.5 && peak > 7000 / Math.Clamp(Sensitivity, 0.4, 2.5))
            {
                _sinceOnset = 0;
                _onsetPeak = rms;
                _onsetAt = _clock;
            }
            else if (rms < _floor * 2.5)
            {
                _floor += (rms - _floor) * 0.02;     // drift toward the room's quiet level
                _floor = Math.Clamp(_floor, 20, 4000);
            }
        }
        _previous = rms;

        if (_fireAt >= 0 && _clock >= _fireAt)
        {
            _fireAt = -1;
            if (_claps.Count == 3)
            {
                _claps.Clear();
                TripleClap?.Invoke();
            }
        }
    }

    void Register(long at)
    {
        _claps.RemoveAll(t => at - t > (MaxGapMs * 3));
        if (_claps.Count > 0 && at - _claps[^1] < MinGapMs) return;   // the same clap ringing
        if (_claps.Count > 0 && at - _claps[^1] > MaxGapMs) _claps.Clear();
        if (_claps.Count == 0 && at - _lastSustained < QuietBeforeMs) return;

        _claps.Add(at);
        if (_claps.Count == 3)
        {
            var first = _claps[1] - _claps[0];
            var second = _claps[2] - _claps[1];
            var ratio = Math.Max(first, second) / (double)Math.Max(1, Math.Min(first, second));
            if (ratio <= MaxGapRatio) _fireAt = at + QuietAfterMs;
            else _claps.RemoveAt(0);
        }
        else if (_claps.Count > 3)
        {
            _claps.Clear();                  // four or more: applause, typing, hammering
            _fireAt = -1;
        }
    }
}
