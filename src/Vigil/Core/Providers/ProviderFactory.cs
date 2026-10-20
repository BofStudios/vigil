namespace Vigil.Core.Providers;

public static class ProviderFactory
{
    /// <summary>The provider the settings point at. On Groq, switching brain also
    /// switches config.Model to the brain's model; picking a model by hand overrides it.</summary>
    public static IProvider Build(Config config) => config.Provider switch
    {
        "anthropic" => new AnthropicProvider(config),
        "openai" => OpenAiCompatibleProvider.OpenAi(config),
        "gemini" => OpenAiCompatibleProvider.Gemini(config),
        "ollama" => new OllamaProvider(config),
        _ => OpenAiCompatibleProvider.Groq(config),
    };

    /// <summary>Something that can turn speech into words, whichever brain is chosen:
    /// Groq's Whisper on the free key first, then OpenAI's. Null when neither key is set.</summary>
    public static ITranscriber? Ears(Config config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            return OpenAiCompatibleProvider.Groq(config);
        if (!string.IsNullOrWhiteSpace(config.OpenaiApiKey))
            return OpenAiCompatibleProvider.OpenAi(config);
        return null;
    }

    /// <summary>Groq's natural English voice, on the same free key as the brain.</summary>
    public static ISpeechSynthesizer? GroqVoice(Config config) =>
        string.IsNullOrWhiteSpace(config.ApiKey) ? null : OpenAiCompatibleProvider.Groq(config);

    /// <summary>A cloud voice, when there is an OpenAI-compatible key to pay for it.</summary>
    public static ISpeechSynthesizer? CloudVoice(Config config) =>
        string.IsNullOrWhiteSpace(config.OpenaiApiKey) ? null : OpenAiCompatibleProvider.OpenAi(config);
}
