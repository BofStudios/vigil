using Vigil.Core;
using Vigil.Core.Providers;

namespace Vigil.Voice;

public enum VoiceState { Off, Idle, Listening, Hearing, Thinking, Speaking }

public sealed record Utterance(string Text, string Language);

/// <summary>The ears and the mouth together: wakes on the phrase, records until the
/// person stops, turns it into words, and speaks the answer back.</summary>
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
            if (speaking) State = VoiceState.Speaking;
            else if (State == VoiceState.Speaking) State = VoiceState.Idle;
        };
    }

    /// <summary>Opens the microphone and starts waiting for "Hey Vigil".</summary>
    public bool Start()
    {
        if (!Microphone.Available)
        {
            Problem?.Invoke("No microphone was found.");
            return false;
        }
        try { Mic.Start(); }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
            Problem?.Invoke("Could not open the microphone: " + ex.Message +
                            ". Check Settings > Privacy & security > Microphone > Let desktop apps access your microphone.");
            return false;
        }
        Wake.Sensitivity = _config.WakeSensitivity;
        Mic.Claps.Sensitivity = _config.ClapSensitivity;
        if (_config.WakeWord && !Wake.Start())
            Problem?.Invoke(Wake.Problem);
        State = VoiceState.Idle;
        return true;
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

    void OnWake(DateTime began, TimeSpan duration)
    {
        if (State is VoiceState.Listening or VoiceState.Hearing) return;
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

    /// <summary>Called by three claps or the talk key: come forward, say a word if asked to,
    /// then listen. A second press while it is listening stops it.</summary>
    public void Summon(bool greet)
    {
        if (State is VoiceState.Listening or VoiceState.Hearing)
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
            var heard = await ListenAsync(TimeSpan.FromSeconds(7), null, false, chime: !greet).ConfigureAwait(false);
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

    /// <summary>The microphone button: listen now, no wake phrase needed.</summary>
    public void ListenNow()
    {
        if (State is VoiceState.Listening or VoiceState.Hearing)
        {
            _listen?.Cancel();
            return;
        }
        Speaker.Stop();
        _ = Task.Run(async () =>
        {
            var heard = await ListenAsync(TimeSpan.FromSeconds(7), null, false, chime: true).ConfigureAwait(false);
            if (heard is not null) Heard?.Invoke(heard);
        });
    }

    /// <summary>Records one utterance and turns it into words. Null when nothing was said.</summary>
    public async Task<Utterance?> ListenAsync(TimeSpan wait, DateTime? since, bool alreadySpoke, bool chime, CancellationToken ct = default)
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
            if (!Mic.Running && !Start()) return null;

            _listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (chime) Speaker.Chime();
            State = VoiceState.Listening;
            byte[]? wav;
            try { wav = await Mic.ListenAsync(wait, since, alreadySpoke, _listen.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { wav = null; }
            if (wav is null)
            {
                State = VoiceState.Idle;
                return null;
            }

            State = VoiceState.Hearing;
            Transcript transcript;
            try { transcript = await ears.TranscribeAsync(wav, _config.VoiceLanguage, _listen.Token).ConfigureAwait(false); }
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
            var language = string.IsNullOrWhiteSpace(transcript.Language) ? SpeechText.GuessLanguage(text, "en") : transcript.Language;
            return new Utterance(text, language);
        }
        finally
        {
            _listen?.Dispose();
            _listen = null;
            _listening.Release();
        }
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
