using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vigil.Core;
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
    }

    void StartVoice(bool quiet)
    {
        if (_voice.On) return;
        if (!_voice.Start()) return;
        App.Note("voice on: wake phrase " + (_voice.WakeListening ? "listening" : "off") + ", claps " + (_config.ClapToWake ? "on" : "off"));
        if (!quiet)
            Toast(_voice.WakeListening ? "Listening for “Hey Vigil”." : "Microphone on. Click the mic to talk.");
        RefreshFooter();
    }

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
        if (_talking)
        {
            EndTalk("Alright, I'll be here.");   // the mic button while talking: that's all
            return;
        }
        // it answers the press with a word, then listens
        _voice.Summon(greet: _config.GreetOnSummon);
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
    // A conversation, not a series of commands: it listens, answers out loud, and listens
    // again - no wake phrase, no button - until you say "that's all", press Esc or the mic.
    bool _talking;
    bool _talkListening;
    int _quietRounds;

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
        if (!_voice.On && !_voice.Start()) return;
        _talking = true;
        _quietRounds = 0;
        if (Busy) _agent?.Stop();
        OnWoken();
        RefreshFooter();
        ShowVoiceState(_voice.State);
        Journal("talk mode", "started");
        _voice.Speaker.Say("Talk mode. I'm listening - just talk, and say that's all when you're done.", "en");
        _ = Task.Run(async () =>
        {
            await _voice.Speaker.WhenDoneAsync().ConfigureAwait(false);
            await Dispatcher.BeginInvoke(TalkListen);
        });
    }

    internal void EndTalk(string? say = null)
    {
        if (!_talking) return;
        _talking = false;
        _voice.CancelListening();
        Journal("talk mode", "ended");
        if (say is not null && _voice.On) _voice.Speaker.Say(say, "en");
        RefreshFooter();
        ShowVoiceState(_voice.State);
    }

    void Journal(string kind, string text)
    {
        if (_config.Journal) Mind.Journal(kind, text);
    }

    /// <summary>Listens for the next thing said, hands it on, and - if it was only a
    /// stop word - listens again. Once it is answering, the end of that answer calls this.</summary>
    async void TalkListen()
    {
        if (!_talking || _talkListening) return;
        _talkListening = true;
        try
        {
            while (_talking && !Busy && !_askPending && !_approvalPending)
            {
                var began = DateTime.UtcNow;
                await Task.Delay(250);   // let the end of its own voice die away first
                if (!_talking || Busy) break;
                var heard = await _voice.ListenAsync(TimeSpan.FromSeconds(25), null, false, chime: false);
                if (!_talking) break;
                if (heard is null)
                {
                    // nothing for a while: a few quiet rounds and it lets go, rather than listening all night
                    if (DateTime.UtcNow - began < TimeSpan.FromSeconds(2)) await Task.Delay(1500);   // no hot loop if the mic is busy
                    if (++_quietRounds >= 3)
                    {
                        EndTalk("It's gone quiet, so I'll stop listening. Just say Hey Vigil when you want me.");
                        break;
                    }
                    continue;
                }
                _quietRounds = 0;
                OnHeard(heard);
                if (Busy) break;   // it is answering; when it has finished, the answer calls this again
            }
        }
        finally { _talkListening = false; }
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

    void ShowVoiceState(VoiceState state)
    {
        _shownState = state;
        var showing = state is VoiceState.Listening or VoiceState.Hearing or VoiceState.Speaking;
        VoiceFace.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        Input.Visibility = showing ? Visibility.Hidden : Visibility.Visible;
        Placeholder.Visibility = showing || Input.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        VoiceText.Text = state switch
        {
            VoiceState.Listening => _talking ? "Listening…   ·   talk mode" : "Listening…",
            VoiceState.Hearing => "Understanding…",
            VoiceState.Speaking => "Speaking   ·   Esc to stop",
            _ => "",
        };
        Pulse(state == VoiceState.Listening);
        Breathe();
        if (state == VoiceState.Listening && _shape == Shape.Pill && !_yielded) GoTo(Shape.Bar);
        UpdateTray();
        RefreshFooter();
    }
}
