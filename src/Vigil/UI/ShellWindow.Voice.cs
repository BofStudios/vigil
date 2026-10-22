using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vigil.Core;
using Vigil.Desktop;
using Vigil.Voice;

namespace Vigil.UI;

/// <summary>Talking to it: "Hey Vigil", the listening bars, the spoken answer, and the
/// follow-up that does not need the wake phrase again.</summary>
public partial class ShellWindow
{
    static readonly Regex StopWords = new(
        @"^\W*(stop|cancel|never ?mind|enough|quiet|shut up|dur|durdur|iptal|vazgeç|yeter|tamam yeter|sus|kes|halt|abbrechen|para|basta|arrête)\W*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly List<Rectangle> _bars = new();
    readonly HashSet<string> _toldNoVoice = new();
    float _micLevel, _speakLevel;
    double _phase;

    void WireVoice()
    {
        _voice.Mic.Level += level => _micLevel = level;
        _voice.Speaker.Level += level => _speakLevel = level;
        _voice.StateChanged += state => Dispatcher.BeginInvoke(() => ShowVoiceState(state));
        _voice.Woken += () => Dispatcher.BeginInvoke(OnWoken);
        _voice.Heard += heard => Dispatcher.BeginInvoke(() => OnHeard(heard));
        _voice.Problem += problem => Dispatcher.BeginInvoke(() => OnVoiceProblem(problem));
        _levelTimer.Tick += (_, _) => AnimateLevels();
        _holdWatch.Tick += (_, _) => WatchHold();
    }

    void StartVoice(bool quiet)
    {
        if (_voice.On) return;
        if (!_voice.Start()) return;
        App.Note("voice on: wake phrase " + (_voice.WakeListening ? "listening" : "off") + ", claps " + (_config.ClapToWake ? "on" : "off"));
        if (!quiet)
            Toast(_voice.WakeListening ? "Listening for “Hey Vigil”." : "Hold Ctrl+< and talk.");
        RefreshFooter();
    }

    /// <summary>The mic button: listen now, until you stop talking. Clicked while it listens, it stops.</summary>
    void ToggleListening()
    {
        if (!_config.EnableVoice)
        {
            Toast("Voice is turned off in settings.");
            return;
        }
        if (!_config.IsReady)
        {
            Summon();
            return;
        }
        if (_shape == Shape.Pill) GoTo(Shape.Bar);
        if (_recording is { Done: false } open)
        {
            open.Release();
            return;
        }
        if (_voice.Listening)
        {
            _voice.CancelListening();
            return;
        }
        if (BeginRecording() is { } recording)
        {
            recording.KeepListening();
            _handsFree = true;
            ShowVoiceState(_voice.State);
        }
    }

    // ============================================================= push to talk
    // Hold Ctrl+< and talk; let go and it is written into the bar and sent. A quick tap instead
    // listens until you stop talking. Pressing it while Vigil speaks cuts it off - that is how you
    // interrupt. The microphone is open only for this: nothing listens in between.
    Microphone.Recording? _recording;
    bool _handsFree;
    DateTime _pressed;
    readonly DispatcherTimer _holdWatch = new() { Interval = TimeSpan.FromMilliseconds(30) };
    static readonly TimeSpan TapLongest = TimeSpan.FromMilliseconds(350);

    Microphone.Recording? BeginRecording()
    {
        if (!_voice.On) StartVoice(quiet: true);
        var recording = _voice.PressToTalk();
        if (recording is null) return null;
        _recording = recording;
        _handsFree = false;
        _ = recording.Result.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (_recording == recording) _recording = null;
            ShowVoiceState(_voice.State);
        }), TaskScheduler.Default);
        return recording;
    }

    /// <summary>Ctrl+&lt; went down.</summary>
    void PressedTalkKey()
    {
        if (_recording is { Done: false } open)
        {
            open.Release();   // a second press ends a tap-started listen
            return;
        }
        if (BeginRecording() is null) return;
        _pressed = DateTime.UtcNow;
        _holdWatch.Start();
        ShowVoiceState(_voice.State);
    }

    /// <summary>Watches for the key coming back up: RegisterHotKey only reports it going down.</summary>
    void WatchHold()
    {
        if ((Native.GetAsyncKeyState((int)VkOem102) & 0x8000) != 0) return;   // still held
        _holdWatch.Stop();
        if (_recording is not { Done: false } recording) return;
        if (DateTime.UtcNow - _pressed < TapLongest)
        {
            recording.KeepListening();   // a tap: listen until they stop talking
            _handsFree = true;
            ShowVoiceState(_voice.State);
        }
        else recording.Release();       // a hold: what was said while holding is the message
    }

    void OnWoken()
    {
        if (_yielded) return;   // a game is full screen: answer by voice, stay out of its way
        _hiddenByUser = false;
        if (!IsVisible) Show();
        if (_shape == Shape.Pill) GoTo(Shape.Bar);
    }

    void OnHeard(Utterance heard)
    {
        if (_askPending)
        {
            AnswerAsk(heard.Text);
            return;
        }
        if (_talking && SpeechText.EndsTalk(heard.Text))
        {
            EndTalk("Of course. I'm here whenever you need me.");
            return;
        }
        if (!_talking && SpeechText.StartsTalk(heard.Text))
        {
            StartTalk();
            return;
        }
        if (StopWords.IsMatch(heard.Text))
        {
            StopEverything();
            return;
        }
        Submit(heard.Text, heard.Language);
    }

    // ================================================================ talk mode
    // A spoken conversation: every answer is said out loud, even a typed question. You talk by
    // holding Ctrl+<, and pressing it while Vigil speaks interrupts it. It never opens the
    // microphone on its own, so it cannot hear itself.
    bool _talking;

    internal bool Talking => _talking;

    internal void ToggleTalk()
    {
        if (_talking) EndTalk("Talk mode off.");
        else StartTalk();
    }

    internal void StartTalk()
    {
        if (_talking) return;
        if (!_config.EnableVoice)
        {
            Toast("Voice is turned off in settings.");
            return;
        }
        if (!_config.IsReady)
        {
            Summon();
            return;
        }
        if (!_voice.On) StartVoice(quiet: true);
        _talking = true;
        OnWoken();
        RefreshFooter();
        ShowVoiceState(_voice.State);
        Journal("talk mode", "started");
        _voice.Speaker.Say("Talk mode. Hold Control and the less-than key while you talk, and let go when you're done.", "en");
    }

    internal void EndTalk(string? say = null)
    {
        if (!_talking) return;
        _talking = false;
        Journal("talk mode", "ended");
        if (say is not null && _voice.On) _voice.Speaker.Say(say, "en");
        RefreshFooter();
        ShowVoiceState(_voice.State);
    }

    void Journal(string kind, string text)
    {
        if (_config.Journal) Mind.Journal(kind, text);
    }

    async void FollowUp(string language, double seconds)
    {
        // a conversation, not a series of commands: after it answers, it keeps listening
        // for a moment, and anything said in that moment needs no wake phrase
        var heard = await _voice.ListenAsync(TimeSpan.FromSeconds(seconds), null, false, chime: false);
        if (heard is not null && !Busy) OnHeard(heard);
    }

    void OnVoiceProblem(string problem)
    {
        App.Note("voice: " + problem);
        if (problem == "GROQ_TERMS")
        {
            if (_toldNoVoice.Add("groq"))
                Toast("For Vigil's natural English voice, accept the Orpheus terms once on your Groq account - " +
                      "Voice menu › “Turn on the natural voice”. Until then the Windows voice speaks.", B("Moderate"));
            return;
        }
        if (problem.StartsWith("NO_VOICE:"))
        {
            var code = problem[9..];
            if (_toldNoVoice.Add(code))
            {
                string name;
                try { name = System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
                catch (System.Globalization.CultureNotFoundException) { name = code; }
                Toast("Windows has no " + name + " voice installed, so another voice is reading it. Add one in Settings › Time & language › Speech, " +
                      "or switch to the cloud voice.");
            }
            return;
        }
        Toast(problem, B("Moderate"));
    }

    // ========================================================== the look of it
    void BuildLevels()
    {
        for (var i = 0; i < 7; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = 4,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = B("CoreSweep"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(i * 5.6, 0, 0, 0),
            };
            _bars.Add(bar);
            Levels.Children.Add(bar);
        }
    }

    double _alive;

    /// <summary>Starts the animation clock when there is something alive to show,
    /// and lets it run down to stillness when there is not.</summary>
    void Breathe()
    {
        if (!_levelTimer.IsEnabled && !Motion.Reduced) _levelTimer.Start();
    }

    /// <summary>One frame of life: the bars follow the voice, and the core's bloom, the
    /// aura around the glass and the line under the bar follow how alive Vigil is.</summary>
    void AnimateLevels()
    {
        var state = _shownState;
        var speaking = state == VoiceState.Speaking;
        var hearing = state is VoiceState.Hearing or VoiceState.Thinking;
        var listening = state == VoiceState.Listening;
        var level = speaking ? _speakLevel : _micLevel;
        _phase += 0.2;

        if (VoiceFace.Visibility == Visibility.Visible)
        {
            double[] shape = { 0.45, 0.7, 0.9, 1.0, 0.9, 0.7, 0.45 };
            for (var i = 0; i < _bars.Count; i++)
            {
                double target;
                if (hearing)
                    target = 4 + 8 * (0.5 + 0.5 * Math.Sin(_phase * 1.3 - i * 0.75));
                else
                {
                    var wobble = 0.72 + 0.28 * Math.Sin(_phase * (1.6 + i * 0.31) + i * 1.7);
                    target = 4 + 20 * Math.Min(1, level * 1.5) * shape[i] * wobble;
                }
                _bars[i].Height += (target - _bars[i].Height) * 0.42;
            }
        }

        // how alive: follows the voice when there is one, a slow breath while working
        var target_ = listening ? 0.4 + 0.6 * Math.Min(1, _micLevel * 1.6)
                    : speaking ? 0.35 + 0.65 * Math.Min(1, _speakLevel * 1.4)
                    : hearing ? 0.45 + 0.15 * Math.Sin(_phase * 0.9)
                    : Busy ? 0.28 + 0.12 * Math.Sin(_phase * 0.7)
                    : 0;
        _alive += (target_ - _alive) * (target_ > _alive ? 0.35 : 0.12);

        var voiceAlive = listening || speaking || hearing;
        CoreBloom.Opacity = _alive;
        BloomScale.ScaleX = BloomScale.ScaleY = 0.75 + 0.45 * _alive;
        Aura.Opacity = voiceAlive && IsShown ? _alive * 0.62 : _alive * 0.14;
        VoiceLine.Opacity = voiceAlive ? Math.Min(1, _alive * 1.3) : 0;
        CoreRingA.Opacity = 0.75 + 0.25 * _alive;

        if (target_ == 0 && _alive < 0.01)
        {
            _alive = 0;
            CoreBloom.Opacity = Aura.Opacity = VoiceLine.Opacity = 0;
            _levelTimer.Stop();
        }
    }

    bool IsShown => (IsVisible || _preview) && _shape != Shape.Pill;

    VoiceState _shownState = VoiceState.Off;
    TalkPill? _pill;

    /// <summary>The pill at the bottom of the screen: your voice as a waveform while it listens.</summary>
    void ShowPill(VoiceState state)
    {
        if (_preview) return;
        if (state == VoiceState.Listening && !_yielded)
        {
            _pill ??= new TalkPill(this);
            _pill.Listen(() => _micLevel,
                _recording is { Done: false } && !_handsFree ? "Listening  ·  let go to send"
                : _recording is { Done: false } ? "Listening  ·  Ctrl+< to stop"
                : "Listening…");
        }
        else if (state == VoiceState.Hearing) _pill?.Hearing();
        else _pill?.Away();
    }

    void ShowVoiceState(VoiceState state)
    {
        _shownState = state;
        var showing = state is VoiceState.Listening or VoiceState.Hearing or VoiceState.Speaking;
        VoiceFace.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        Input.Visibility = showing ? Visibility.Hidden : Visibility.Visible;
        Placeholder.Visibility = showing || Input.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        VoiceText.Text = state switch
        {
            VoiceState.Listening => _recording is { Done: false } && !_handsFree ? "Listening…   ·   let go to send"
                                  : _recording is { Done: false } ? "Listening…   ·   Ctrl+< to stop"
                                  : _talking ? "Listening…   ·   talk mode" : "Listening…",
            VoiceState.Hearing => "Understanding…",
            VoiceState.Speaking => "Speaking   ·   Esc to stop, or hold Ctrl+< to answer",
            _ => "",
        };
        Pulse(state == VoiceState.Listening);
        ShowPill(state);
        Breathe();
        if (state == VoiceState.Listening && _shape == Shape.Pill && !_yielded) GoTo(Shape.Bar);
        UpdateTray();
        RefreshFooter();
    }
}
