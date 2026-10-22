using Vigil.Core;

namespace Vigil.UI;

/// <summary>What the app window (HomeWindow) may ask of the bar. The bar stays the one place
/// that owns the agent, the voice and the settings; the app window is a view onto them.</summary>
public partial class ShellWindow
{
    HomeWindow? _home;

    /// <summary>Opens the app window, or brings it forward.</summary>
    public void OpenHome()
    {
        if (_home is null)
        {
            _home = new HomeWindow(this);
            _home.Closed += (_, _) => _home = null;
            _home.Show();
        }
        else
        {
            if (_home.WindowState == System.Windows.WindowState.Minimized) _home.WindowState = System.Windows.WindowState.Normal;
            _home.Activate();
        }
    }

    internal Config Settings => _config;

    internal void Change(Action<Config> change)
    {
        change(_config);
        Save();
        _agent?.RefreshSystemPrompt();
        RefreshFooter();
    }

    internal bool WakeOn => _config.WakeWord && _voice.WakeListening;
    internal void SetWake(bool on) { if (WakeOn != on) ToggleWake(); }
    internal void SetClaps(bool on) { if (_config.ClapToWake != on) ToggleClaps(); }
    internal bool English => AnswersInEnglish;

    // ----------------------------------------------------------- the voice
    internal bool ElevenLabsReady => _voice.Speaker.ElevenLabsReady;

    /// <summary>A new ElevenLabs key (empty removes it). Says a line with it, so you hear it worked.</summary>
    internal void SetElevenLabsKey(string key)
    {
        Change(c => c.ElevenLabsApiKey = key.Trim());
        _voice.Speaker.RetryElevenLabs();
        if (key.Trim().Length > 0) SayHello();
        else Toast("ElevenLabs key removed.");
    }

    internal void SetElevenLabsVoice(string id)
    {
        Change(c => c.ElevenLabsVoice = id);
        _voice.Speaker.RetryElevenLabs();
        SayHello();
    }

    internal void SayHello() => _voice.Speaker.Say(Hello(), SpeechLanguage);

    internal void OpenElevenLabsKeys() => OpenUrl(Voice.ElevenLabs.KeysUrl);
    internal void SetEnglish(bool on) { if (AnswersInEnglish != on) ToggleEnglish(); }
    internal void SetApproval(string mode) => SetMode(mode);
    internal void ForgetToday() { Activity.Forget(); Toast("Today's activity is forgotten."); }

    internal string LastCheckIn => _checkInMessage;
    internal void ReplyCheckIn(bool talk) => ReplyToCheckIn(talk);

    internal void AskFromHome(string text)
    {
        Summon();
        Submit(text, null);
    }

    internal void TalkFromHome() => TalkKey();

    internal void OpenBrainSetup()
    {
        Summon();
        ShowSetup();
    }

    internal string BrainDescription
    {
        get
        {
            var route = Routes.Get(_config.Provider);
            return route.Name + " · " + (_agent?.Provider.Model ?? _config.ActiveModel) + " · " + Brains.Get(_config.Brain).Name;
        }
    }
}
