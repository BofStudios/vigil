using System.Windows;
using System.Windows.Threading;
using Vigil.Core;
using Vigil.Core.Providers;

namespace Vigil.UI;

/// <summary>The friend part: noticing which app is in front, and checking in now and then.</summary>
public partial class ShellWindow
{
    CheckIn? _checkIn;
    readonly DispatcherTimer _minute = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer _checkInLife = new() { Interval = TimeSpan.FromSeconds(75) };
    int _samples;
    bool _checkingIn;

    void WireFriend()
    {
        _checkIn = new CheckIn(_config);
        _work = new WorkMode(_config);
        if (_config.WorkMode) _work.Resume();
        _minute.Tick += async (_, _) => await MaybeCheckIn();
        _minute.Start();
        _checkInLife.Tick += (_, _) => HideCheckIn();
        CheckInLater.Click += (_, _) => HideCheckIn();
        CheckInReply.Click += (_, _) => ReplyToCheckIn(talk: false);
        CheckInTalk.Click += (_, _) => ReplyToCheckIn(talk: true);
    }

    /// <summary>From Watch, twice a second: a sample every five seconds is plenty.</summary>
    void SampleActivity(IntPtr foreground)
    {
        if (!_config.Awareness || ++_samples % 10 != 0) return;
        Activity.Sample(foreground, _hwnd);
    }

    async Task MaybeCheckIn()
    {
        if (_config.DoNotDisturb && !_config.Quiet)
        {
            // its time ran out: back to normal, said quietly
            _config.DoNotDisturb = false;
            _config.QuietUntil = null;
            Save();
            RefreshFooter();
        }
        if (_checkingIn || _checkIn is null || !_config.IsReady || _config.Quiet) return;
        if (Busy || _approvalPending || _askPending || _voice.State is not (Voice.VoiceState.Off or Voice.VoiceState.Idle)) return;
        if (_work?.On == true)
        {
            await MaybeAdvise();
            return;
        }
        if (_yielded) return;
        var moment = _checkIn.Due();
        if (moment is null) return;

        _checkingIn = true;
        try
        {
            IProvider provider;
            try { provider = _agent?.Provider ?? ProviderFactory.Build(_config); }
            catch (ProviderException) { return; }
            var text = await _checkIn.WriteAsync(provider, moment, CancellationToken.None);
            if (Busy || _yielded) return;   // something started meanwhile: it can wait for the next one
            ShowCheckIn(text);
        }
        finally { _checkingIn = false; }
    }

    string _checkInMessage = "";

    void ShowCheckIn(string text, bool work = false)
    {
        if (_config.Quiet) return;   // turned on while it was being written
        _checkInMessage = text;
        if (_config.Journal) Mind.Journal(work ? "work mode" : "checked in", text);
        // put away on purpose, or a game is full screen: a quiet note by the clock instead.
        // Work mode was asked to interrupt, so only a full-screen game holds it back.
        if (_yielded || (_hiddenByUser && !work))
        {
            _tray.Tell("Vigil", text);
            return;
        }
        if (!IsVisible) { _tucked = false; Show(); }
        CheckInLabel.Visibility = work ? Visibility.Visible : Visibility.Collapsed;
        CheckInText.Text = text;
        CheckInCard.Visibility = Visibility.Visible;
        CheckInCard.To(OpacityProperty, 1, 260);
        _checkInLife.Stop();
        _checkInLife.Start();
        if (work ? _config.EnableVoice : _config.SpeakCheckIns && _voice.On) _voice.Speaker.Say(text, "en");
    }

    // ============================================================== work mode
    WorkMode? _work;

    internal bool InWorkMode => _work?.On == true;

    async Task MaybeAdvise()
    {
        if (_work is null || !_work.Due()) return;
        _checkingIn = true;
        try
        {
            IProvider provider;
            try { provider = _agent?.Provider ?? ProviderFactory.Build(_config); }
            catch (ProviderException) { return; }
            var text = await _work.AdviseAsync(provider, CancellationToken.None);
            if (Busy || !_work.On) return;   // something started meanwhile, or it was turned off
            ShowCheckIn(text, work: true);
        }
        finally { _checkingIn = false; }
    }

    internal void ToggleWork()
    {
        if (InWorkMode) EndWork();
        else _ = StartWork();
    }

    /// <summary>Asks what the work is - it makes every piece of advice sharper - then starts watching.</summary>
    internal async Task StartWork()
    {
        if (InWorkMode || _work is null) return;
        if (!_config.IsReady)
        {
            Summon();
            return;
        }
        if (!_config.Awareness)
        {
            _config.Awareness = true;   // work mode is built on it: app names and minutes, nothing more
            Toast("Work mode needs Vigil to know which app is in front (names and minutes only) - turned that on.");
        }
        Summon();
        var goal = await AskOnce("Work mode. What are you working on? It makes the advice sharper.", new[] { "Skip" });
        if (goal is null) return;   // closed without an answer
        _work.Begin(goal.Equals("Skip", StringComparison.OrdinalIgnoreCase) ? "" : goal);
        Save();
        RefreshFooter();
        if (_config.Journal) Mind.Journal("work mode", "started" + (_config.WorkGoal.Length > 0 ? ": " + _config.WorkGoal : ""));
        var first = _config.WorkMinutes + " minutes";
        Toast(_config.Quiet
            ? "Work mode on - it watches, but says nothing while Do not disturb is on."
            : "Work mode on. I'll watch how it goes and step in after " + first + ".");
    }

    internal void EndWork()
    {
        if (!InWorkMode || _work is null) return;
        var summary = _work.End();
        Save();
        RefreshFooter();
        if (_config.Journal) Mind.Journal("work mode", summary);
        Toast(summary);
    }

    /// <summary>A question from Vigil itself, outside any task: the same card the agent asks with.</summary>
    async Task<string?> AskOnce(string question, IReadOnlyList<string> choices)
    {
        if (_askPending) return null;
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowAsk(question, choices, answer);
        var result = await answer.Task;
        _askPending = false;
        AskCard.Fade(false, 140);
        PlaceholderText.Text = "Ask Vigil anything…";
        PlaceholderHint.Text = "   or hold Ctrl+< and talk";
        UpdateTray();
        return result;
    }

    void HideCheckIn()
    {
        _checkInLife.Stop();
        CheckInCard.To(OpacityProperty, 0, 180, 0, () => CheckInCard.Visibility = Visibility.Collapsed);
    }

    /// <summary>Carries the check-in into a conversation, so "yes, help me" means something.</summary>
    void ReplyToCheckIn(bool talk)
    {
        HideCheckIn();
        if (!EnsureAgent()) return;
        _agent!.Remember(_checkInMessage);
        var box = NewAnswerBox();
        box.Document = Markdown.Render(_checkInMessage, B("TextBrush"), B("TextDim"), B("Sunken"),
                                       (System.Windows.Media.FontFamily)FindResource("Sans"), (System.Windows.Media.FontFamily)FindResource("Mono"));
        Thread.Children.Add(box);
        Summon();
        GoTo(Shape.Panel, focus: !talk);
        if (talk)
        {
            if (BeginRecording() is { } recording)
            {
                recording.KeepListening();
                _handsFree = true;
            }
        }
    }
}
