using System.Globalization;
using System.Speech.Recognition;

namespace Vigil.Voice;

/// <summary>Listens for "Hey Vigil" - on this machine, with the speech recogniser
/// built into Windows. Nothing is sent anywhere to do it; the words that follow the
/// phrase are the first thing that ever leaves.</summary>
public sealed class WakeWord : IDisposable
{
    static readonly string[] Phrases = { "hey vigil", "hi vigil", "okay vigil", "ok vigil" };

    SpeechRecognitionEngine? _engine;

    /// <summary>Raised when the phrase is heard: (when the utterance began, how long it was).
    /// A long utterance means the request came in the same breath.</summary>
    public event Action<DateTime, TimeSpan>? Heard;

    public double Sensitivity { get; set; } = 0.55;
    /// <summary>While Vigil itself is talking, it takes more to wake it - its own voice
    /// should not be able to interrupt it.</summary>
    public bool Strict { get; set; }
    public bool Running { get; private set; }
    public string Problem { get; private set; } = "";

    public static RecognizerInfo? FindRecognizer()
    {
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            return installed.FirstOrDefault(r => r.Culture.Name == "en-US")
                   ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                   ?? installed.FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    public bool Start()
    {
        if (Running) return true;
        var recogniser = FindRecognizer();
        if (recogniser is null)
        {
            Problem = "Windows has no speech recogniser installed. Add one in Settings > Time & language > Speech.";
            return false;
        }
        try
        {
            var engine = new SpeechRecognitionEngine(recogniser);
            var phrase = new Choices(Phrases);

            // the phrase on its own, and the phrase followed by anything at all
            engine.LoadGrammar(new Grammar(new GrammarBuilder(phrase) { Culture = recogniser.Culture }) { Name = "wake" });
            var followed = new GrammarBuilder(phrase) { Culture = recogniser.Culture };
            followed.AppendWildcard();
            engine.LoadGrammar(new Grammar(followed) { Name = "wake+" });

            engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(250);
            engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(450);
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += OnRecognized;
            engine.RecognizeAsync(RecognizeMode.Multiple);
            _engine = engine;
            Running = true;
            Problem = "";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
        {
            Problem = "Could not listen for the wake phrase: " + ex.Message;
            Stop();
            return false;
        }
    }

    void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var needed = Strict ? Math.Max(0.82, Sensitivity) : Sensitivity;
        var heardPhrase = e.Result.Words.Take(2).Where(w => w.Text != "...").ToList();
        var phraseConfidence = heardPhrase.Count == 0 ? e.Result.Confidence : heardPhrase.Average(w => w.Confidence);
        if (Math.Max(e.Result.Confidence, phraseConfidence) < needed) return;

        var duration = e.Result.Audio?.Duration ?? TimeSpan.FromSeconds(1);
        // the event arrives just after the utterance ends; reach back to its start, with room to spare
        var began = DateTime.UtcNow - duration - TimeSpan.FromMilliseconds(700);
        Heard?.Invoke(began, duration);
    }

    public void Stop()
    {
        Running = false;
        var engine = _engine;
        _engine = null;
        if (engine is null) return;
        try
        {
            engine.SpeechRecognized -= OnRecognized;
            engine.RecognizeAsyncCancel();
        }
        catch (Exception) { }
        engine.Dispose();
    }

    public void Dispose() => Stop();

    public static string Culture => FindRecognizer()?.Culture.Name ?? CultureInfo.CurrentCulture.Name;
}
