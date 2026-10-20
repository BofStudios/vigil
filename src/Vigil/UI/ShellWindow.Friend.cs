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
        if (_checkingIn || _checkIn is null || !_config.IsReady) return;
        if (Busy || _approvalPending || _askPending || _yielded || _voice.State is not (Voice.VoiceState.Off or Voice.VoiceState.Idle)) return;
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

    void ShowCheckIn(string text)
    {
        _checkInMessage = text;
        if (_config.Journal) Mind.Journal("checked in", text);
        if (_hiddenByUser)
        {
            _tray.Tell("Vigil", text);   // put away on purpose: a quiet note by the clock instead
            return;
        }
        if (!IsVisible) Show();
        CheckInText.Text = text;
        CheckInCard.Visibility = Visibility.Visible;
        CheckInCard.To(OpacityProperty, 1, 260);
        _checkInLife.Stop();
        _checkInLife.Start();
        if (_config.SpeakCheckIns && _voice.On) _voice.Speaker.Say(text, "en");
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
            if (!_voice.On) StartVoice(quiet: true);
            _voice.Summon(greet: false);
        }
    }
}
