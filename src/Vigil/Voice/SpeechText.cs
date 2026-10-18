using System.Text;
using System.Text.RegularExpressions;

namespace Vigil.Voice;

/// <summary>Turning written answers into things worth saying, and heard words into
/// a clean request. Pure text in, text out - which is what makes it testable.</summary>
public static class SpeechText
{
    static readonly Regex Wake = new(
        @"^\W*(?:(?:hey|hi|hay|hei|okay|ok|o\.k\.)\W*)?(?:vigil|vigill|vijil|vicil|visil|vigel|virgil|wigil|vigilo)\b[\s,.!?:;-]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whisper's well-known inventions when it is handed silence or hum.</summary>
    static readonly Regex Hallucination = new(
        @"^\W*(thank you( very much)?( for watching)?|thanks for watching|you|bye|altyaz[ıi] m\.?k\.?|abone ol(mayı unutmayın)?|" +
        @"sous-titres.*|untertitel.*|subtitles by.*|\[(music|silence|blank_audio)\]|\(music\)|\.+)\W*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Hey Vigil, open Notepad" -> "open Notepad".</summary>
    public static string StripWake(string heard)
    {
        var text = heard.Trim();
        var stripped = Wake.Replace(text, "", 1).Trim();
        return stripped.Length > 0 && char.IsLower(stripped[0]) && stripped != text
            ? char.ToUpper(stripped[0]) + stripped[1..]
            : stripped;
    }

    public static bool IsNoise(string heard) =>
        string.IsNullOrWhiteSpace(heard) || Hallucination.IsMatch(heard.Trim());

    /// <summary>Markdown, code and links read out loud are gibberish. This keeps the words.</summary>
    public static string ForSpeech(string text)
    {
        var spoken = Regex.Replace(text, @"```[\s\S]*?(```|$)", " ");          // code blocks
        spoken = Regex.Replace(spoken, @"`([^`]*)`", "$1");                      // inline code
        spoken = Regex.Replace(spoken, @"!\[[^\]]*\]\([^)]*\)", " ");           // images
        spoken = Regex.Replace(spoken, @"\[([^\]]+)\]\([^)]*\)", "$1");          // links keep their words
        spoken = Regex.Replace(spoken, @"https?://\S+", " ");                    // bare addresses
        spoken = Regex.Replace(spoken, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        spoken = Regex.Replace(spoken, @"^\s*>\s?", "", RegexOptions.Multiline);
        spoken = Regex.Replace(spoken, @"^\s*(?:[-*+•]|\d+[.)])\s+", "", RegexOptions.Multiline);
        spoken = Regex.Replace(spoken, @"\|", " ");
        spoken = Regex.Replace(spoken, @"(\*\*|__|\*|_|~~)(?=\S)(.+?)(?<=\S)\1", "$2");
        spoken = Regex.Replace(spoken, @"[☀-➿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]|️", "");  // emoji
        spoken = Regex.Replace(spoken, @"[ \t]+", " ");
        spoken = Regex.Replace(spoken, @"\s*\n\s*", "\n");
        return spoken.Trim();
    }

    /// <summary>A guess at the language of a reply, from letters only that language uses.
    /// Good enough to pick a voice; the spoken language is used when there is one.</summary>
    public static string GuessLanguage(string text, string fallback)
    {
        if (Regex.IsMatch(text, "[ğĞşŞıİ]")) return "tr";
        if (Regex.IsMatch(text, @"[Ѐ-ӿ]")) return "ru";
        if (Regex.IsMatch(text, @"[؀-ۿ]")) return "ar";
        if (Regex.IsMatch(text, @"[぀-ヿ]")) return "ja";
        if (Regex.IsMatch(text, @"[가-힯]")) return "ko";
        if (Regex.IsMatch(text, @"[一-鿿]")) return "zh";
        if (Regex.IsMatch(text, @"[Ͱ-Ͽ]")) return "el";
        if (Regex.IsMatch(text, @"[ßäöü]") && Regex.IsMatch(text, @"\b(der|die|das|und|ist|nicht)\b", RegexOptions.IgnoreCase)) return "de";
        if (Regex.IsMatch(text, @"[ñ¿¡]")) return "es";
        if (Regex.IsMatch(text, @"[àâçèêëîïôûœ]") && Regex.IsMatch(text, @"\b(le|la|les|est|et|je|vous)\b", RegexOptions.IgnoreCase)) return "fr";
        return fallback;
    }
}

/// <summary>Cuts a stream of text into sentences as it arrives, so the first one can
/// be spoken while the model is still writing the rest. That is most of what makes
/// a voice feel like it is talking rather than reading.</summary>
public sealed class SentenceSplitter
{
    static readonly Regex Boundary = new(@"(?<=[.!?…。！？;:])[""'”’)\]]*\s+|\n+", RegexOptions.CultureInvariant);
    const int SoftLimit = 220;

    readonly StringBuilder _buffer = new();
    bool _inCode;

    /// <summary>Add streamed text; returns any sentences now complete.</summary>
    public IEnumerable<string> Push(string chunk)
    {
        _buffer.Append(chunk);
        var ready = new List<string>();
        while (true)
        {
            var text = _buffer.ToString();

            // never speak a code block - skip it whole, even across chunks
            var fence = text.IndexOf("```", StringComparison.Ordinal);
            if (_inCode)
            {
                if (fence < 0) { _buffer.Clear(); _buffer.Append(text.Length >= 2 ? text[^2..] : text); break; }
                _buffer.Remove(0, fence + 3);
                _inCode = false;
                continue;
            }
            if (fence >= 0)
            {
                Add(ready, text[..fence]);
                _buffer.Remove(0, fence + 3);
                _inCode = true;
                continue;
            }

            var match = Boundary.Match(text);
            if (match.Success && match.Index > 0)
            {
                Add(ready, text[..(match.Index + match.Length)]);
                _buffer.Remove(0, match.Index + match.Length);
                continue;
            }
            if (text.Length > SoftLimit)
            {
                var cut = text.LastIndexOfAny(new[] { ',', ';', ' ' }, SoftLimit);
                if (cut < 40) cut = SoftLimit;
                Add(ready, text[..(cut + 1)]);
                _buffer.Remove(0, cut + 1);
                continue;
            }
            break;
        }
        return ready;
    }

    public IEnumerable<string> Flush()
    {
        var rest = _inCode ? "" : _buffer.ToString();
        _buffer.Clear();
        _inCode = false;
        var ready = new List<string>();
        Add(ready, rest);
        return ready;
    }

    static void Add(List<string> ready, string text)
    {
        var spoken = SpeechText.ForSpeech(text);
        if (spoken.Any(char.IsLetterOrDigit))
            ready.Add(spoken);
    }
}
