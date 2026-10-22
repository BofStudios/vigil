using Vigil.Core;
using Vigil.Core.Providers;

namespace Vigil.Voice;

public enum VoiceState { Off, Idle, Listening, Hearing, Thinking, Speaking }

public sealed record Utterance(string Text, string Language);

/// <summary>The ears and the mouth together.
///
/// By default Vigil listens only while you hold Ctrl+&lt; (or after a tap of it, or a click
/// of the mic, until you stop talking) - the microphone is closed the rest of the time.
/// "Hey Vigil" and three claps are there for anyone who wants them always open.</summary>
public sealed class VoiceController : IDisposable
{
    static readonly TimeSpan ShortWake = TimeSpan.FromSeconds(1.5);

    readonly Config _config;
    readonly SemaphoreSlim _listening = new(1, 1);
    CancellationTokenSource? _listen;
    VoiceState _state = VoiceState.Off;

    public Microphone Mic { get; } = new();
    public WakeWord Wake { get; } = new();
    public Speaker Speaker { get; }

    public event Action<VoiceState>? StateChanged;
    /// <summary>The wake phrase was heard; the window should come forward.</summary>
    public event Action? Woken;
    /// <summary>A spoken request, ready for the agent.</summary>
    public event Action<Utterance>? Heard;
    public event Action<string>? Problem;
    /// <summary>The words so far, while the person is still talking - each one replaces the last,
    /// and later words can change earlier ones. Not the final text: that comes through Heard.</summary>
    public event Action<string>? Partial;

    public VoiceState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public bool On => State != VoiceState.Off;

    public VoiceController(Config config)
    {
        _config = config;
        Speaker = new Speaker(config);
        Wake.Heard += OnWake;
        Mic.Claps.TripleClap += () =>
        {
            if (_config.ClapToWake) Summon(greet: _config.GreetOnSummon);
        };
        Mic.Failed += message => Problem?.Invoke(message);
        Speaker.Failed += message => Problem?.Invoke(message);
        Speaker.SpeakingChanged += speaking =>
        {
            Wake.Strict = speaking;
            // its own voice must never count as three claps: deaf to them while it speaks, and for a moment after
            Mic.ClapsDeafUntil = speaking ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            if (speaking) State = VoiceState.Speaking;
            else if (State == VoiceState.Speaking) State = VoiceState.Idle;
        };
    }

    /// <summary>Gets voice ready. The microphone itself opens only if something needs it open all
    /// the time (three claps); "Hey Vigil" uses Windows' own recogniser.</summary>
    public bool Start()
    {
        if (!Microphone.Available)
        {
            Problem?.Invoke("No microphone was found.");
            return false;
        }
        Wake.Sensitivity = _config.WakeSensitivity;
        Mic.Claps.Sensitivity = _config.ClapSensitivity;
        if (_config.ClapToWake && !OpenMic()) return false;
        if (_config.WakeWord && !Wake.Start())
            Problem?.Invoke(Wake.Problem);
        State = VoiceState.Idle;
        return true;
    }

    /// <summary>Follows the settings after they change: opens or closes what is always on.</summary>
    public void Refresh()
    {
        if (!On) return;
        if (_config.WakeWord) { if (!Wake.Running && !Wake.Start()) Problem?.Invoke(Wake.Problem); }
        else Wake.Stop();
        if (_config.ClapToWake) OpenMic();
        else if (_listening.CurrentCount == 1) Mic.Stop();   // not in the middle of listening
    }

    bool OpenMic()
    {
        try
        {
            Mic.Start();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
            Problem?.Invoke("Could not open the microphone: " + ex.Message +
                            ". Check Settings > Privacy & security > Microphone > Let desktop apps access your microphone.");
            return false;
        }
    }

    /// <summary>Closes the microphone when nothing needs it open between turns.</summary>
    void CloseMicIfIdle()
    {
        if (!_config.ClapToWake) Mic.Stop();
    }

    public void Stop()
    {
        _listen?.Cancel();
        Wake.Stop();
        Mic.Stop();
        Speaker.Stop();
        State = VoiceState.Off;
    }

    public bool WakeListening => Wake.Running;

    public bool Listening => State is VoiceState.Listening or VoiceState.Hearing;

    void OnWake(DateTime began, TimeSpan duration)
    {
        if (Listening) return;
        if (Speaker.Speaking) Speaker.Stop();   // "Hey Vigil" while it talks: stop and listen
        Woken?.Invoke();
        var oneBreath = duration > ShortWake;
        _ = Task.Run(async () =>
        {
            var heard = await ListenAsync(oneBreath ? TimeSpan.FromSeconds(1.3) : TimeSpan.FromSeconds(6),
                                          since: began, alreadySpoke: oneBreath, chime: !oneBreath).ConfigureAwait(false);
            if (heard is not null) Heard?.Invoke(heard);
        });
    }

    /// <summary>Called by three claps: come forward, say a word if asked to, then listen.</summary>
    public void Summon(bool greet)
    {
        if (Listening)
        {
            _listen?.Cancel();
            return;
        }
        Speaker.Stop();
        Woken?.Invoke();
        _ = Task.Run(async () =>
        {
            if (greet)
            {
                Speaker.Say(Greeting(), "en");
                await Speaker.WhenDoneAsync().ConfigureAwait(false);
                await Task.Delay(120).ConfigureAwait(false);   // let the room go quiet before listening
            }
            var heard = await ListenAsync(TimeSpan.FromSeconds(7), null, false, chime: !greet, conversation: true).ConfigureAwait(false);
            if (heard is not null) Heard?.Invoke(heard);
        });
    }

    static readonly string[] Hellos =
    {
        "At your service.", "Online and listening.", "Go ahead, I'm listening.", "Ready when you are.", "Yes? I'm here.",
    };

    static string Greeting()
    {
        var hour = DateTime.Now.Hour;
        var hello = Hellos[Random.Shared.Next(Hellos.Length)];
        if (Random.Shared.Next(3) != 0) return hello;
        var time = hour < 5 ? "Working late, I see." : hour < 12 ? "Good morning." : hour < 18 ? "Good afternoon." : "Good evening.";
        return time + " " + hello;
    }

    /// <summary>Stops listening right now, if it is.</summary>
    public void CancelListening() => _listen?.Cancel();

    // ============================================================ push to talk
    /// <summary>Starts recording now, for as long as the key is held: call Release() when it is let
    /// go, or KeepListening() after a tap to listen until the person stops talking. Cuts off
    /// whatever Vigil was saying - pressing to talk is how you interrupt it. Null if it can't listen.</summary>
    public Microphone.Recording? PressToTalk()
    {
        Speaker.Stop();
        if (!_listening.Wait(0))
        {
            // something else is listening (a follow-up, the wake phrase): the key wins
            _listen?.Cancel();
            if (!_listening.Wait(400)) return null;
        }
        var ears = ProviderFactory.Ears(_config);
        if (ears is null)
        {
            _listening.Release();
            Problem?.Invoke("To understand speech Vigil needs a Groq key (free) or an OpenAI key. Add one in settings.");
            return null;
        }
        if ((State == VoiceState.Off && !Start()) || !OpenMic())
        {
            _listening.Release();
            return null;
        }
        _listen = new CancellationTokenSource();
        var recording = Mic.Record(TimeSpan.FromSeconds(7), _listen.Token);
        State = VoiceState.Listening;
        _ = FinishAsync(recording, ears, _listen.Token);
        _ = LiveAsync(recording, ears, _listen.Token);
        return recording;
    }

    // Groq's free Whisper allows 20 requests a minute. Live words use what is spare and always leave
    // room for the final one - the one that matters.
    static readonly Queue<DateTime> WhisperCalls = new();
    const int WhisperPerMinute = 20, KeepForFinals = 5;

    static bool MayTranscribe(bool final)
    {
        lock (WhisperCalls)
        {
            var now = DateTime.UtcNow;
            while (WhisperCalls.Count > 0 && now - WhisperCalls.Peek() > TimeSpan.FromSeconds(60)) WhisperCalls.Dequeue();
            if (WhisperCalls.Count >= (final ? WhisperPerMinute : WhisperPerMinute - KeepForFinals)) return final;
            WhisperCalls.Enqueue(now);
            return true;
        }
    }

    /// <summary>While the key is held: every second or so, the whole of what has been said so far is
    /// turned into words again, so the text grows as you talk and corrects itself as more context comes.</summary>
    async Task LiveAsync(Microphone.Recording recording, ITranscriber ears, CancellationToken ct)
    {
        var lastSeconds = 0.0;
        try
        {
            while (!recording.Done)
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
                if (recording.Done) break;
                var seconds = recording.Seconds;
                if (seconds < 0.8 || seconds - lastSeconds < 0.6) continue;   // nothing new worth asking about
                if (recording.SoFar() is not { } wav) continue;               // nobody has spoken yet
                if (!MayTranscribe(final: false)) continue;
                lastSeconds = seconds;
                Transcript partial;
                try { partial = await ears.TranscribeAsync(wav, _config.VoiceLanguage, ct).ConfigureAwait(false); }
                catch (ProviderException) { break; }   // rate limit or a hiccup: the final one still comes
                if (recording.Done) break;              // the final one is on its way: no older guess over it
                var text = SpeechText.StripWake(partial.Text).Trim();
                if (text.Length > 0 && !SpeechText.IsNoise(text)) Partial?.Invoke(text);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task FinishAsync(Microphone.Recording recording, ITranscriber ears, CancellationToken ct)
    {
        try
        {
            byte[]? wav;
            try { wav = await recording.Result.ConfigureAwait(false); }
            catch (OperationCanceledException) { wav = null; }
            if (wav is null) { State = VoiceState.Idle; return; }
            var heard = await TranscribeAsync(wav, ears, ct).ConfigureAwait(false);
            if (heard is not null) Heard?.Invoke(heard);
        }
        finally
        {
            _listen?.Dispose();
            _listen = null;
            CloseMicIfIdle();
            _listening.Release();
        }
    }

    // ================================================================ listening
    /// <summary>Records one utterance, until the person stops talking, and turns it into words.
    /// Null when nothing was said.</summary>
    public async Task<Utterance?> ListenAsync(TimeSpan wait, DateTime? since, bool alreadySpoke, bool chime, CancellationToken ct = default,
                                              bool conversation = false)
    {
        if (!await _listening.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        var ears = ProviderFactory.Ears(_config);
        try
        {
            if (ears is null)
            {
                Problem?.Invoke("To understand speech Vigil needs a Groq key (free) or an OpenAI key. Add one in settings.");
                return null;
            }
            if ((State == VoiceState.Off && !Start()) || !OpenMic()) return null;

            _listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (conversation)
            {
                // straight after Vigil has spoken the room still rings: let it die away first, or the
                // tail of its own voice is taken for someone speaking
                try { await Mic.WaitForQuietAsync(TimeSpan.FromSeconds(1.6), _listen.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { State = VoiceState.Idle; return null; }
            }
            if (chime) Speaker.Chime();
            State = VoiceState.Listening;
            byte[]? wav;
            try { wav = await Mic.ListenAsync(wait, since, alreadySpoke, _listen.Token, conversation).ConfigureAwait(false); }
            catch (OperationCanceledException) { wav = null; }
            if (wav is null)
            {
                State = VoiceState.Idle;
                return null;
            }
            return await TranscribeAsync(wav, ears, _listen.Token).ConfigureAwait(false);
        }
        finally
        {
            _listen?.Dispose();
            _listen = null;
            CloseMicIfIdle();
            _listening.Release();
        }
    }

    async Task<Utterance?> TranscribeAsync(byte[] wav, ITranscriber ears, CancellationToken ct)
    {
        State = VoiceState.Hearing;
        MayTranscribe(final: true);
        Transcript transcript;
        try { transcript = await ears.TranscribeAsync(wav, _config.VoiceLanguage, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { State = VoiceState.Idle; return null; }
        catch (ProviderException ex)
        {
            Problem?.Invoke("Could not make out the words: " + ex.Message);
            State = VoiceState.Idle;
            return null;
        }

        var text = SpeechText.StripWake(transcript.Text);
        State = VoiceState.Idle;
        if (SpeechText.IsNoise(text)) return null;
        // Vigil's own voice coming back in: right after it spoke, and nearly word for word what it said
        if (DateTime.UtcNow - Speaker.LastSpokeEnd < TimeSpan.FromSeconds(8) &&
            SpeechText.IsEcho(text, Speaker.RecentSpeech(TimeSpan.FromSeconds(45))))
        {
            Vigil.App.Note("voice: ignored its own echo: " + text);
            return null;
        }
        var language = string.IsNullOrWhiteSpace(transcript.Language) ? SpeechText.GuessLanguage(text, "en") : transcript.Language;
        return new Utterance(text, language);
    }

    /// <summary>Marks the stretch between a question and the first spoken word of the answer.</summary>
    public void Thinking() => State = VoiceState.Thinking;

    public void Settle()
    {
        if (State is VoiceState.Thinking) State = Speaker.Speaking ? VoiceState.Speaking : VoiceState.Idle;
    }

    public void Dispose()
    {
        Stop();
        Wake.Dispose();
        Mic.Dispose();
        Speaker.Dispose();
    }
}
