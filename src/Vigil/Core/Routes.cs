namespace Vigil.Core;

/// <summary>One way in, and everything the first screen needs to draw it.
/// Vigil is the harness, not the model: whatever thinks, it thinks behind the
/// same approvals and the same blocked actions.</summary>
public sealed record Route(
    string Key,
    string Name,
    string Badge,
    string Blurb,
    IReadOnlyList<(string Id, string Note)> Models,
    string Link,
    string LinkText,
    string Placeholder,
    bool NeedsKey = true);

public static class Routes
{
    public static readonly IReadOnlyDictionary<string, string> ClaudeModels = new Dictionary<string, string>
    {
        ["claude-sonnet-5"] = "Balanced: the one to use (default)",
        ["claude-opus-5"] = "Most capable, and the slowest",
        ["claude-haiku-4-5-20251001"] = "Fast and cheap, still calls tools",
    };

    public static readonly IReadOnlyDictionary<string, string> OpenAiModels = new Dictionary<string, string>
    {
        ["gpt-6-astra"] = "Strongest at driving a computer (default)",
        ["gpt-5.2"] = "Cheaper, still calls tools well",
        ["gpt-5.2-mini"] = "Fast and cheap",
    };

    public static readonly IReadOnlyDictionary<string, string> GroqModels = new Dictionary<string, string>
    {
        ["openai/gpt-oss-120b"] = "Strong reasoning + tool use (default)",
        ["openai/gpt-oss-20b"] = "Faster and lighter, still supports tools",
        ["qwen/qwen3.8-27b"] = "Multilingual, tool use + vision",
        ["qwen/qwen3.6-27b"] = "Multilingual, tool use",
    };

    public static readonly Route Free = new(
        "groq", "Free", "no card",
        "Groq's free tier, and it stays free. Two models come with it - Vigil moves between " +
        "them when you switch how it thinks. It also hears you: speech goes to Whisper on the same key.",
        new[] { ("openai/gpt-oss-20b", "fast, for everyday jobs"), ("openai/gpt-oss-120b", "stronger, for problems") },
        "https://console.groq.com/keys", "Get a free key", "gsk_…");

    public static readonly Route Claude = new(
        "anthropic", "Claude", "your key",
        "Bring your own Anthropic key and Vigil runs on Claude. Not free, but it is the strongest " +
        "thing you can point at your own machine.",
        ClaudeModels.Select(m => (m.Key, m.Value)).ToArray(),
        "https://console.anthropic.com/settings/keys", "Get a Claude key", "sk-ant-…");

    public static readonly Route Gpt = new(
        "openai", "GPT", "your key",
        "Your own OpenAI key. When a lab ships something better at driving a computer, you run it " +
        "in here, behind the same approvals and the same blocked actions.",
        OpenAiModels.Select(m => (m.Key, m.Value)).ToArray(),
        "https://platform.openai.com/api-keys", "Get an OpenAI key", "sk-…");

    public static readonly Route Offline = new(
        "ollama", "Offline", "local",
        "Ollama runs the model on this computer. Nothing is sent anywhere at all - no key, no account, no network.",
        new[] { (Config.DefaultOllamaModel, "pull it with: ollama pull " + Config.DefaultOllamaModel) },
        "https://ollama.com/download", "Install Ollama", "http://localhost:11434", NeedsKey: false);

    public static readonly IReadOnlyList<Route> All = new[] { Free, Claude, Gpt, Offline };

    public static Route Get(string? key) =>
        All.FirstOrDefault(r => r.Key == (key ?? "").Trim().ToLowerInvariant()) ?? Free;
}
