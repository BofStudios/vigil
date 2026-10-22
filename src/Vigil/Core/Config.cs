using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigil.Core;

/// <summary>User settings, in ~/.vigil/config.json.
///
/// The field names are the same snake_case ones the first Vigil wrote, so a key
/// saved by the old version is picked up by this one without asking again.</summary>
public sealed class Config
{
    public const string DefaultModel = "openai/gpt-oss-120b";
    public const string DefaultVisionModel = "qwen/qwen3.8-27b";
    public const string DefaultAnthropicModel = "claude-sonnet-5";
    public const string DefaultOpenAiModel = "gpt-6-astra";
    public const string DefaultGeminiModel = "gemini-2.5-flash";
    public const string DefaultOllamaHost = "http://localhost:11434";
    public const string DefaultOllamaModel = "qwen3:8b";
    public const string DefaultOllamaVisionModel = "llama3.2-vision";

    public static readonly string[] ApprovalModes = { "ask", "auto", "yolo" };
    public static readonly string[] Providers = { "groq", "gemini", "anthropic", "openai", "ollama" };

    // ------------------------------------------------------------- the brain
    public string Provider { get; set; } = "groq";
    /// <summary>The Groq key. Named api_key because that is what v1 called it.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = DefaultModel;
    public string VisionModel { get; set; } = DefaultVisionModel;

    public string AnthropicApiKey { get; set; } = "";
    public string AnthropicModel { get; set; } = DefaultAnthropicModel;

    /// <summary>OpenAI, or anything that speaks its API (set openai_base_url).</summary>
    public string OpenaiApiKey { get; set; } = "";
    public string OpenaiModel { get; set; } = DefaultOpenAiModel;
    public string OpenaiBaseUrl { get; set; } = "";

    /// <summary>Google Gemini, through its OpenAI-compatible endpoint.</summary>
    public string GeminiApiKey { get; set; } = "";
    public string GeminiModel { get; set; } = DefaultGeminiModel;

    public string OllamaHost { get; set; } = DefaultOllamaHost;
    public string OllamaModel { get; set; } = DefaultOllamaModel;
    public string OllamaVisionModel { get; set; } = DefaultOllamaVisionModel;

    // ------------------------------------------------------------- behaviour
    /// <summary>auto by default: routine work runs unattended, while taking the
    /// mouse, keyboard or screen is confirmed every time regardless.</summary>
    public string ApprovalMode { get; set; } = "auto";
    /// <summary>direct follows instructions, autonomous works out the route.
    /// Changes behaviour, never security.</summary>
    public string Brain { get; set; } = "direct";
    public double Temperature { get; set; } = 0.3;
    public int MaxSteps { get; set; } = 40;
    public int MaxToolOutput { get; set; } = 12000;
    public int MaxHistoryMessages { get; set; } = 60;

    public bool EnableGui { get; set; } = true;
    public bool EnableBrowser { get; set; } = true;
    public bool EnableSystem { get; set; } = true;
    public bool EnableMemory { get; set; } = true;
    public bool EnablePlanner { get; set; } = true;
    public List<string> ProtectPaths { get; set; } = new();

    // ----------------------------------------------------------------- voice
    public bool EnableVoice { get; set; } = true;
    /// <summary>Listen for "Hey Vigil" all the time. Off by default: Vigil listens while Ctrl+&lt; is held.
    /// The wake phrase is recognised on this machine; nothing leaves it until the phrase has been heard.</summary>
    public bool WakeWord { get; set; } = false;
    /// <summary>0..1 - how sure the recogniser has to be before it wakes.</summary>
    public double WakeSensitivity { get; set; } = 0.55;
    /// <summary>Empty means: work out the language from what was said.</summary>
    public string VoiceLanguage { get; set; } = "";
    /// <summary>Read answers aloud when the question was spoken.</summary>
    public bool SpeakReplies { get; set; } = true;
    /// <summary>auto picks the most natural voice it can reach: ElevenLabs with a key, then a neural voice
    /// on this computer (piper), Groq, OpenAI, Windows. elevenlabs, piper, windows, groq and cloud force one.</summary>
    public string TtsEngine { get; set; } = "auto";
    public string ElevenLabsApiKey { get; set; } = "";
    /// <summary>An ElevenLabs voice id; Daniel - British, deep, calm - by default.</summary>
    public string ElevenLabsVoice { get; set; } = "onwK4e9ZLuTAKqWW03F9";
    /// <summary>Flash: the quickest to start speaking, and it speaks every language.</summary>
    public string ElevenLabsModel { get; set; } = "eleven_flash_v2_5";
    /// <summary>Part of the file name of a Piper voice in ~/.vigil/voices/piper, e.g. "alan"; empty takes the first one found.</summary>
    public string PiperVoice { get; set; } = "";
    public string GroqTtsModel { get; set; } = "canopylabs/orpheus-v1-english";
    public string GroqTtsVoice { get; set; } = "daniel";
    /// <summary>A Windows voice by name; empty picks the best one for the language.</summary>
    public string TtsVoice { get; set; } = "";
    public double SpeechRate { get; set; } = 1.0;
    /// <summary>1.0 is the voice as shipped; a touch lower sounds calmer.</summary>
    public double SpeechPitch { get; set; } = 0.92;
    public string CloudTtsModel { get; set; } = "gpt-4o-mini-tts";
    public string CloudTtsVoice { get; set; } = "onyx";
    public string CloudTtsStyle { get; set; } =
        "Calm, articulate and warm, with a measured pace and a quiet, dry wit - an unflappable butler who also runs the computer.";
    /// <summary>After Vigil speaks, keep listening this long for a follow-up without the wake phrase.</summary>
    /// <summary>0 (the default): after it speaks, the microphone stays closed until you hold Ctrl+&lt; again.</summary>
    public double FollowUpSeconds { get; set; } = 0;
    /// <summary>Three claps in a row open Vigil and start listening - needs the microphone open all the time.</summary>
    public bool ClapToWake { get; set; } = false;
    /// <summary>Which voice defaults this file has been brought up to.</summary>
    public int VoiceVersion { get; set; }
    /// <summary>1 is the default; higher hears quieter claps.</summary>
    public double ClapSensitivity { get; set; } = 1.0;
    /// <summary>Say a short hello when summoned by claps, before listening.</summary>
    public bool GreetOnSummon { get; set; } = true;
    /// <summary>"en" answers in English whatever language it is spoken to in; "auto" answers in the user's language.</summary>
    public string ReplyLanguage { get; set; } = "en";

    // ------------------------------------------------------------ friend
    /// <summary>What Vigil calls you. Empty until you tell it.</summary>
    public string UserName { get; set; } = "";
    /// <summary>Where Vigil Mind lives. Empty means Documents\Vigil Mind.</summary>
    public string MindFolder { get; set; } = "";
    /// <summary>Write what was asked and done into Vigil Mind\Journal.</summary>
    public bool Journal { get; set; } = true;
    /// <summary>Know which app is in front (names and minutes only).</summary>
    public bool Awareness { get; set; } = true;
    /// <summary>Now and then, check in like a friend would.</summary>
    public bool CheckIns { get; set; } = true;
    /// <summary>How long in one app before a check-in, and the least time between two.</summary>
    public int CheckInMinutes { get; set; } = 50;
    /// <summary>Say check-ins out loud as well as showing them.</summary>
    public bool SpeakCheckIns { get; set; } = false;
    /// <summary>Do not disturb: no check-ins, no advice, nothing said unasked. Vigil still answers when asked.</summary>
    public bool DoNotDisturb { get; set; } = false;
    /// <summary>When do-not-disturb ends by itself; null keeps it on until it is turned off.</summary>
    public DateTime? QuietUntil { get; set; }

    /// <summary>Is do-not-disturb on right now?</summary>
    [JsonIgnore]
    public bool Quiet => DoNotDisturb && (QuietUntil is null || DateTime.Now < QuietUntil);

    // ---------------------------------------------------------------- window
    public bool StartHidden { get; set; } = false;

    // ------------------------------------------------------------------ load
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static Config Load()
    {
        Config config;
        try
        {
            config = File.Exists(Paths.ConfigFile)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.ConfigFile), Json) ?? new Config()
                : new Config();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            config = new Config();
        }
        config.ApplyEnvironment();
        config.Repair();
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Home);
        var temp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, Paths.ConfigFile, overwrite: true);
    }

    void ApplyEnvironment()
    {
        static string? Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        ApiKey = Env("GROQ_API_KEY") ?? ApiKey;
        AnthropicApiKey = Env("ANTHROPIC_API_KEY") ?? AnthropicApiKey;
        OpenaiApiKey = Env("OPENAI_API_KEY") ?? OpenaiApiKey;
        GeminiApiKey = Env("GEMINI_API_KEY") ?? GeminiApiKey;
        ElevenLabsApiKey = Env("ELEVENLABS_API_KEY") ?? ElevenLabsApiKey;
        OpenaiBaseUrl = Env("OPENAI_BASE_URL") ?? OpenaiBaseUrl;
        OllamaHost = Env("OLLAMA_HOST") ?? OllamaHost;
        Model = Env("VIGIL_MODEL") ?? Model;
        if (Env("VIGIL_PROVIDER") is { } provider && Providers.Contains(provider))
            Provider = provider;
        if (Env("VIGIL_APPROVAL_MODE") is { } mode && ApprovalModes.Contains(mode))
            ApprovalMode = mode;
    }

    /// <summary>A hand-edited file should not be able to put Vigil in a state it cannot reason about.</summary>
    void Repair()
    {
        if (!Providers.Contains(Provider)) Provider = "groq";
        if (!ApprovalModes.Contains(ApprovalMode)) ApprovalMode = "auto";
        Brain = Core.Brains.Get(Brain).Key;
        MaxSteps = Math.Clamp(MaxSteps, 1, 200);
        MaxToolOutput = Math.Clamp(MaxToolOutput, 1000, 200_000);
        MaxHistoryMessages = Math.Clamp(MaxHistoryMessages, 8, 400);
        WakeSensitivity = Math.Clamp(WakeSensitivity, 0.2, 0.95);
        SpeechRate = Math.Clamp(SpeechRate, 0.5, 2.0);
        SpeechPitch = Math.Clamp(SpeechPitch, 0.5, 1.5);
        FollowUpSeconds = Math.Clamp(FollowUpSeconds, 0, 30);
        ClapSensitivity = Math.Clamp(ClapSensitivity, 0.4, 2.5);
        CheckInMinutes = Math.Clamp(CheckInMinutes, 10, 480);
        if (string.IsNullOrWhiteSpace(ReplyLanguage)) ReplyLanguage = "en";
        ProtectPaths ??= new();
        if (VoiceVersion < 3)
        {
            // voice 3: push to talk. Nothing listens all the time unless it is switched back on
            WakeWord = false;
            ClapToWake = false;
            FollowUpSeconds = 0;
            VoiceVersion = 3;
            Migrated = true;
        }
    }

    /// <summary>Set when Load brought an older file up to date, so it is saved once.</summary>
    [JsonIgnore]
    public bool Migrated { get; private set; }

    // ------------------------------------------------- the active provider's models
    [JsonIgnore]
    public string ActiveModel
    {
        get => Provider switch
        {
            "ollama" => OllamaModel,
            "anthropic" => AnthropicModel,
            "openai" => OpenaiModel,
            "gemini" => GeminiModel,
            _ => Model,
        };
        set
        {
            switch (Provider)
            {
                case "ollama": OllamaModel = value; break;
                case "anthropic": AnthropicModel = value; break;
                case "openai": OpenaiModel = value; break;
                case "gemini": GeminiModel = value; break;
                default: Model = value; break;
            }
        }
    }

    [JsonIgnore]
    public string ActiveVisionModel => Provider switch
    {
        "ollama" => OllamaVisionModel,
        "anthropic" => AnthropicModel,  // Claude sees for itself
        "openai" => OpenaiModel,        // so does this one
        "gemini" => GeminiModel,
        _ => VisionModel,
    };

    /// <summary>Whether the selected provider has what it needs to start.</summary>
    [JsonIgnore]
    public bool IsReady => Provider switch
    {
        "ollama" => true,
        "anthropic" => AnthropicApiKey.Length > 0,
        "openai" => OpenaiApiKey.Length > 0,
        "gemini" => GeminiApiKey.Length > 0,
        _ => ApiKey.Length > 0,
    };

    public Config Clone() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this, Json), Json)!;
}
