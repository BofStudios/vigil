using System.Collections.Concurrent;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vigil.Core;
using Vigil.Core.Providers;
using WinSpeech = Windows.Media.SpeechSynthesis;

namespace Vigil.Voice;

/// <summary>Vigil's voice.
///
/// Answers are spoken sentence by sentence while the model is still writing:
/// the first sentence is being said while the second is being synthesised and
/// the third is being written. One continuous output stream plays them back to
/// back, so there is no click or gap between sentences.
///
/// Two voices: the Windows ones (free, on this machine, any language Windows has
/// a voice for) and a cloud voice through an OpenAI-compatible key, which sounds
/// closest to a person and speaks every language.</summary>
public sealed class Speaker : IDisposable
{
    const int OutputRate = 24000;

    readonly Config _config;
    readonly ClipQueue _queue = new(WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 1));
    readonly object _lock = new();
    IWavePlayer? _output;
    Channel<string>? _sentences;
    SentenceSplitter _splitter = new();
    CancellationTokenSource _cancel = new();
    Task _synthesis = Task.CompletedTask;
    string _language = "en";
    int _generation;
    int _pending;   // sentences written but not yet queued for playback

    public event Action<bool>? SpeakingChanged;
    /// <summary>0..1, how loud Vigil is right now - drives the waveform.</summary>
    public event Action<float>? Level;
    public event Action<string>? Failed;

    public bool Speaking { get; private set; }

    public Speaker(Config config)
    {
        _config = config;
        _queue.Level += level => Level?.Invoke(level);
        _queue.Drained += () => SetSpeaking(false);
    }

    // ------------------------------------------------------------ voices
    public static IReadOnlyList<WinSpeech.VoiceInformation> WindowsVoices()
    {
        try { return WinSpeech.SpeechSynthesizer.AllVoices.ToList(); }
        catch (Exception) { return Array.Empty<WinSpeech.VoiceInformation>(); }
    }

    /// <summary>English prefers a British man - the nearest thing Windows ships to a
    /// butler - then any man, then anyone. Other languages take what is installed.</summary>
    static readonly string[] Preferred = { "George", "Ryan", "Thomas", "Mark", "Guy", "David", "Tolga" };

    public WinSpeech.VoiceInformation? VoiceFor(string language)
    {
        var voices = WindowsVoices();
        if (voices.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(_config.TtsVoice))
        {
            var chosen = voices.FirstOrDefault(v => v.DisplayName.Contains(_config.TtsVoice, StringComparison.OrdinalIgnoreCase));
            if (chosen is not null) return chosen;
        }
        var code = Code(language);
        var matching = voices.Where(v => v.Language.StartsWith(code, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0) return null;
        return matching
            .OrderBy(v => code == "en" && v.Language.Equals("en-GB", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(v => v.Gender == WinSpeech.VoiceGender.Male ? 0 : 1)
            .ThenBy(v => { var i = Array.FindIndex(Preferred, p => v.DisplayName.Contains(p)); return i < 0 ? 99 : i; })
            .First();
    }

    /// <summary>"turkish" or "tr-TR" -> "tr".</summary>
    public static string Code(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "en";
        var trimmed = language.Trim();
        if (trimmed.Length >= 2 && (trimmed.Length == 2 || trimmed[2] == '-')) return trimmed[..2].ToLowerInvariant();
        var match = System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.EnglishName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        return match?.TwoLetterISOLanguageName ?? "en";
    }

    PiperVoice? _piper;
    volatile bool _piperFailed;
    volatile bool _elevenFailed;

    /// <summary>Try ElevenLabs again - after a new key, say.</summary>
    public void RetryElevenLabs() => _elevenFailed = false;

    /// <summary>Is ElevenLabs the voice that will speak?</summary>
    public bool ElevenLabsReady => _config.TtsEngine is "auto" or "elevenlabs" && !_elevenFailed
                                   && !string.IsNullOrWhiteSpace(_config.ElevenLabsApiKey);

    /// <summary>Is the local neural voice in place and in use?</summary>
    public bool LocalVoiceReady => _config.TtsEngine is "auto" or "piper" && !_piperFailed && PiperVoice.Installed(_config.PiperVoice);

    /// <summary>"alan" from en_GB-alan-medium.onnx - what to call the voice in a menu.</summary>
    public string LocalVoiceName => PiperVoice.Model(_config.PiperVoice) is { } m
        ? Path.GetFileNameWithoutExtension(m).Replace('_', ' ').Replace('-', ' ')
        : "";

    /// <summary>Set once Groq has refused its voice - not asked again this session.</summary>
    volatile bool _groqRefused;
    volatile string? _workingGroqVoice;

    /// <summary>Try Groq's voice again - after its terms have been accepted, say.</summary>
    public void RetryNaturalVoice() => _groqRefused = false;

    public const string GroqTermsUrl = "https://console.groq.com/playground?model=canopylabs%2Forpheus-v1-english";

    // ----------------------------------------------------------- speaking
    /// <summary>Starts a new spoken answer, cutting off whatever was being said.</summary>
    public void Begin(string language)
    {
        Stop();
        lock (_lock)
        {
            _language = Code(language);
            _splitter = new SentenceSplitter();
            _cancel = new CancellationTokenSource();
            _sentences = Channel.CreateUnbounded<string>();
            var generation = ++_generation;
            var reader = _sentences.Reader;
            var token = _cancel.Token;
            _synthesis = Task.Run(() => SynthesiseAll(reader, generation, token));
        }
    }

    /// <summary>More of the answer, as it streams in.</summary>
    public void Feed(string text)
    {
        lock (_lock)
        {
            if (_sentences is null) return;
            foreach (var sentence in _splitter.Push(text))
                Write(sentence);
        }
    }

    /// <summary>The answer is complete; say whatever is left.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (_sentences is null) return;
            foreach (var sentence in _splitter.Flush())
                Write(sentence);
            _sentences.Writer.TryComplete();
        }
    }

    readonly List<(DateTime At, string Text)> _said = new();

    /// <summary>When Vigil last finished speaking.</summary>
    public DateTime LastSpokeEnd { get; private set; } = DateTime.MinValue;

    /// <summary>What Vigil said in the last little while - so its own voice, coming back in through
    /// the microphone, can be told from the person.</summary>
    public string RecentSpeech(TimeSpan within)
    {
        lock (_said)
        {
            var from = DateTime.UtcNow - within;
            return string.Join(" ", _said.Where(s => s.At >= from).Select(s => s.Text));
        }
    }

    void Write(string sentence)
    {
        lock (_said)
        {
            _said.Add((DateTime.UtcNow, sentence));
            if (_said.Count > 16) _said.RemoveAt(0);
        }
        Interlocked.Increment(ref _pending);
        if (!_sentences!.Writer.TryWrite(sentence)) Interlocked.Decrement(ref _pending);
    }

    /// <summary>Waits until nothing is waiting to be said - without closing the answer,
    /// so more can follow (a question asked half way through a spoken reply).</summary>
    public async Task IdleAsync(CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            while ((Volatile.Read(ref _pending) > 0 || Speaking) && !ct.IsCancellationRequested)
                await Task.Delay(60, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public void Say(string text, string language)
    {
        Begin(language);
        Feed(text);
        Finish();
    }

    /// <summary>Waits until everything queued has been said (or it was cut off).</summary>
    public async Task WhenDoneAsync(CancellationToken ct = default)
    {
        Task synthesis;
        lock (_lock) synthesis = _synthesis;
        try { await synthesis.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        // never wait for ever: if the device has stopped pulling, "speaking" would never end
        var giveUp = DateTime.UtcNow + _queue.Remaining + TimeSpan.FromSeconds(3);
        while (Speaking && !ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow > giveUp)
            {
                Stop();
                break;
            }
            await Task.Delay(50, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }
    }

    /// <summary>Silence, now.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _generation++;
            _cancel.Cancel();
            _sentences?.Writer.TryComplete();
            _sentences = null;
            Interlocked.Exchange(ref _pending, 0);
        }
        _queue.Clear();
        SetSpeaking(false);
    }

    async Task SynthesiseAll(ChannelReader<string> reader, int generation, CancellationToken ct)
    {
        try
        {
            await foreach (var sentence in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var language = SpeechText.GuessLanguage(sentence, _language);
                    byte[] wav;
                    try { wav = await SynthesiseAsync(sentence, language, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Failed?.Invoke(ex.Message);
                        continue;
                    }
                    if (wav.Length == 0 || generation != _generation || ct.IsCancellationRequested) continue;
                    Enqueue(wav);
                }
                finally
                {
                    if (generation == _generation && Volatile.Read(ref _pending) > 0) Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The most natural voice available for this language: Groq's for English on the
    /// free key, OpenAI's for everything when there is a key for it, and Windows' own otherwise.</summary>
    internal async Task<byte[]> SynthesiseAsync(string text, string language, CancellationToken ct)
    {
        var code = Code(language);
        var engine = _config.TtsEngine;

        // ElevenLabs, with a key: the most natural voice there is, in every language
        if (engine is "auto" or "elevenlabs" && !_elevenFailed && !string.IsNullOrWhiteSpace(_config.ElevenLabsApiKey))
        {
            try
            {
                return await ElevenLabs.SpeakAsync(_config.ElevenLabsApiKey, _config.ElevenLabsVoice, _config.ElevenLabsModel, text, ct)
                                       .ConfigureAwait(false);
            }
            catch (ProviderException ex)
            {
                _elevenFailed = true;   // a bad key or an empty allowance: the next voice takes over, and it is said once
                Failed?.Invoke(ex.Message + " - another voice is speaking for now.");
            }
        }

        // a neural voice on this computer: nothing leaves the machine, no key, no terms
        if (engine is "auto" or "piper" && !_piperFailed && PiperVoice.Model(_config.PiperVoice) is { } piperModel
            && PiperVoice.LanguageOf(piperModel) == code && PiperVoice.Exe() is not null)
        {
            try { return await (_piper ??= new PiperVoice()).SpeakAsync(text, _config.PiperVoice, _config.SpeechRate, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                _piperFailed = true;   // not asked again this session; the next voice takes over
                Failed?.Invoke("The local voice stopped working (" + ex.Message + "), so another voice is speaking.");
            }
        }

        if ((engine is "auto" or "groq") && code == "en" && !_groqRefused && ProviderFactory.GroqVoice(_config) is { } groq)
        {
            // the configured voice first; if Groq has renamed its voices, the others it is known to offer
            foreach (var voiceName in new[] { _workingGroqVoice ?? _config.GroqTtsVoice, "troy", "austin", "daniel" }.Distinct())
            {
                try
                {
                    var wav = await groq.SpeakAsync(text, _config.GroqTtsModel, voiceName, "", ct).ConfigureAwait(false);
                    _workingGroqVoice = voiceName;
                    return wav;
                }
                catch (ProviderException ex) when (ex.Message.Contains("voice", StringComparison.OrdinalIgnoreCase)
                                                   && !ex.Message.Contains("terms", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                catch (ProviderException ex)
                {
                    _groqRefused = true;
                    Failed?.Invoke(ex.Message.Contains("terms", StringComparison.OrdinalIgnoreCase) ? "GROQ_TERMS" : "Groq voice: " + ex.Message);
                    break;
                }
            }
            if (_workingGroqVoice is null) _groqRefused = true;
        }

        if ((engine is "auto" or "cloud") && ProviderFactory.CloudVoice(_config) is { } cloud)
            return await cloud.SpeakAsync(text, _config.CloudTtsModel, _config.CloudTtsVoice, _config.CloudTtsStyle, ct).ConfigureAwait(false);

        var voice = VoiceFor(language) ?? VoiceFor("en");
        if (voice is null) throw new InvalidOperationException("Windows has no voices installed.");
        if (!voice.Language.StartsWith(Code(language), StringComparison.OrdinalIgnoreCase) && Code(language) != "en")
            Failed?.Invoke("NO_VOICE:" + Code(language));

        using var synth = new WinSpeech.SpeechSynthesizer { Voice = voice };
        synth.Options.SpeakingRate = Math.Clamp(_config.SpeechRate, 0.5, 2.0);
        synth.Options.AudioPitch = Math.Clamp(_config.SpeechPitch, 0.5, 1.5);
        synth.Options.AppendedSilence = WinSpeech.SpeechAppendedSilence.Min;
        synth.Options.PunctuationSilence = WinSpeech.SpeechPunctuationSilence.Min;
        using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct).ConfigureAwait(false);
        using var read = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    internal static ISampleProvider? Decode(byte[] wav, Action<string>? failed = null)
    {
        try
        {
            var reader = new WaveFileReader(new MemoryStream(wav));
            var seconds = reader.TotalTime.TotalSeconds;
            ISampleProvider clip = reader.ToSampleProvider();
            if (clip.WaveFormat.Channels == 2) clip = new StereoToMonoSampleProvider(clip);
            if (clip.WaveFormat.SampleRate != OutputRate) clip = new WdlResamplingSampleProvider(clip, OutputRate);
            return new Measured(clip, (long)(seconds * OutputRate));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
        {
            failed?.Invoke("Could not play the voice: " + ex.Message);
            return null;
        }
    }

    void Enqueue(byte[] wav)
    {
        ISampleProvider clip;
        try
        {
            var reader = new WaveFileReader(new MemoryStream(wav));
            var seconds = reader.TotalTime.TotalSeconds;
            clip = reader.ToSampleProvider();
            if (clip.WaveFormat.Channels == 2) clip = new StereoToMonoSampleProvider(clip);
            if (clip.WaveFormat.SampleRate != OutputRate) clip = new WdlResamplingSampleProvider(clip, OutputRate);
            clip = new Measured(clip, (long)(seconds * OutputRate));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
        {
            Failed?.Invoke("Could not play the voice: " + ex.Message);
            return;
        }
        Play(clip);
    }

    /// <summary>A soft rising two-note chime: "I'm listening."</summary>
    public void Chime(bool rising = true)
    {
        var notes = rising ? new[] { 784.0, 1175.0 } : new[] { 1175.0, 784.0 };
        var samples = new List<float>();
        foreach (var frequency in notes)
        {
            var length = (int)(OutputRate * 0.085);
            for (var i = 0; i < length; i++)
            {
                var t = i / (double)OutputRate;
                var envelope = Math.Min(1, i / (OutputRate * 0.008)) * Math.Exp(-t * 26);
                samples.Add((float)(Math.Sin(2 * Math.PI * frequency * t) * envelope * 0.16));
            }
        }
        Play(new FloatClip(samples.ToArray(), _queue.WaveFormat), speaking: false);
    }

    void Play(ISampleProvider clip, bool speaking = true)
    {
        _queue.Add(clip);
        if (speaking) SetSpeaking(true);
        lock (_lock)
        {
            if (_output is not null) return;
            try
            {
                // WASAPI, not the old waveOut: on some sound cards waveOut keeps replaying its last few hundred
                // milliseconds after the sentence ends - the last syllable, over and over, measured on this PC
                var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 80);
                output.PlaybackStopped += (_, e) =>
                {
                    // the device went away or the stream failed: drop it, so the next sentence opens a fresh one.
                    // This runs on the playback thread itself - no lock here, and no disposing it from inside
                    // its own thread: either would wait for ever on the one doing the stopping.
                    if (Interlocked.CompareExchange(ref _output, null, output) != output) return;   // stopped on purpose
                    _ = Task.Run(() => { try { output.Dispose(); } catch (Exception) { } });
                    _queue.Clear();
                    SetSpeaking(false);
                    if (e.Exception is not null) Failed?.Invoke("The speaker stopped: " + e.Exception.Message);
                };
                output.Init(_queue);
                output.Play();
                _output = output;
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _output?.Dispose();
                _output = null;
                Failed?.Invoke("No speaker to play on: " + ex.Message);
            }
        }
    }

    void SetSpeaking(bool speaking)
    {
        if (Speaking == speaking) return;
        Speaking = speaking;
        if (!speaking) LastSpokeEnd = DateTime.UtcNow;
        SpeakingChanged?.Invoke(speaking);
    }

    public void Dispose()
    {
        Stop();
        _piper?.Dispose();
        // taken out first and stopped outside the lock: stopping waits for the playback thread
        var output = Interlocked.Exchange(ref _output, null);
        try
        {
            output?.Stop();
            output?.Dispose();
        }
        catch (Exception) { }
    }

    // ---------------------------------------------------------- playback
    /// <summary>Plays clips one after another, forever, and silence in between.
    /// One device stream for the whole session means no pop at the start of a sentence.</summary>
    internal sealed class ClipQueue(WaveFormat format) : ISampleProvider
    {
        readonly ConcurrentQueue<ISampleProvider> _clips = new();
        ISampleProvider? _current;
        bool _wasPlaying;
        int _quietReads;

        public WaveFormat WaveFormat { get; } = format;
        public event Action<float>? Level;
        public event Action? Drained;

        long _queuedSamples, _playedSamples;

        /// <summary>Roughly how long until everything queued has been played.</summary>
        public TimeSpan Remaining => TimeSpan.FromSeconds(Math.Max(0, Interlocked.Read(ref _queuedSamples) - Interlocked.Read(ref _playedSamples))
                                                          / (double)WaveFormat.SampleRate);

        public void Add(ISampleProvider clip)
        {
            // a clip whose length is known counts towards Remaining; others count as ten seconds
            var samples = clip is ISampleLength known ? known.Samples : WaveFormat.SampleRate * 10L;
            Interlocked.Add(ref _queuedSamples, samples);
            _clips.Enqueue(clip);
        }

        public void Clear()
        {
            while (_clips.TryDequeue(out _)) { }
            _current = null;
            Interlocked.Exchange(ref _queuedSamples, 0);
            Interlocked.Exchange(ref _playedSamples, 0);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                var current = _current;   // Clear() may null it from another thread
                if (current is null)
                {
                    if (!_clips.TryDequeue(out current)) break;
                    _current = current;
                }
                var read = current.Read(buffer, offset + written, count - written);
                if (read == 0) Interlocked.CompareExchange(ref _current, null, current);
                written += read;
            }
            if (written < count) Array.Clear(buffer, offset + written, count - written);
            Interlocked.Add(ref _playedSamples, written);

            float peak = 0;
            for (var i = 0; i < written; i++) peak = Math.Max(peak, Math.Abs(buffer[offset + i]));
            Level?.Invoke(Math.Min(1f, peak * 1.6f));

            if (written > 0) { _wasPlaying = true; _quietReads = 0; }
            else if (_wasPlaying && ++_quietReads > 2)
            {
                // a short grace period: the next sentence may still be on its way
                _wasPlaying = false;
                Drained?.Invoke();
            }
            return count;
        }
    }

    /// <summary>A clip that knows how long it is.</summary>
    interface ISampleLength
    {
        long Samples { get; }
    }

    /// <summary>Wraps a clip with its length, so the queue can tell how long is left.</summary>
    sealed class Measured(ISampleProvider inner, long samples) : ISampleProvider, ISampleLength
    {
        public long Samples => samples;
        public WaveFormat WaveFormat => inner.WaveFormat;
        public int Read(float[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    }

    sealed class FloatClip(float[] samples, WaveFormat format) : ISampleProvider, ISampleLength
    {
        public long Samples => samples.Length;
        int _position;
        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
