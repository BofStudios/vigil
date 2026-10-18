using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vigil.Core.Providers;

/// <summary>Anything that speaks the OpenAI chat API: Groq (the free default),
/// OpenAI itself, and through openai_base_url OpenRouter, LM Studio, vLLM and
/// the rest. One implementation, because it is one protocol.</summary>
public class OpenAiCompatibleProvider : IProvider, ITranscriber, ISpeechSynthesizer
{
    const int MaxAttempts = 3;
    static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    readonly string _baseUrl;
    readonly string _apiKey;
    readonly bool _isGroq;
    bool _omitTemperature;
    long _tokens;

    public string Name { get; }
    public string Model { get; set; }
    public string VisionModel { get; set; }
    public double Temperature { get; set; }
    public string SpeechModel { get; set; }
    /// <summary>Groq counts tokens per minute per model, so when one is out of breath its sibling usually is not.</summary>
    public string? Sibling => !_isGroq ? null : Model switch
    {
        "openai/gpt-oss-20b" => "openai/gpt-oss-120b",
        "openai/gpt-oss-120b" => "openai/gpt-oss-20b",
        _ => null,
    };
    public long TotalTokens => Interlocked.Read(ref _tokens);

    public OpenAiCompatibleProvider(string name, string baseUrl, string apiKey, string model,
                                    string visionModel, double temperature, string speechModel)
    {
        Name = name;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _isGroq = name == "groq";
        Model = model;
        VisionModel = string.IsNullOrWhiteSpace(visionModel) ? model : visionModel;
        Temperature = temperature;
        SpeechModel = speechModel;
    }

    public static OpenAiCompatibleProvider Groq(Config c)
    {
        if (string.IsNullOrWhiteSpace(c.ApiKey))
            throw new AuthException("No Groq key yet. Get a free one at https://console.groq.com/keys");
        return new("groq", "https://api.groq.com/openai/v1", c.ApiKey, c.Model, c.VisionModel,
                   c.Temperature, "whisper-large-v3-turbo");
    }

    public static OpenAiCompatibleProvider OpenAi(Config c)
    {
        if (string.IsNullOrWhiteSpace(c.OpenaiApiKey))
            throw new AuthException("No OpenAI key. Get one at https://platform.openai.com/api-keys");
        var url = string.IsNullOrWhiteSpace(c.OpenaiBaseUrl) ? "https://api.openai.com/v1" : c.OpenaiBaseUrl;
        return new("openai", url, c.OpenaiApiKey, c.OpenaiModel, c.OpenaiModel, c.Temperature, "whisper-1");
    }

    IEnumerable<KeyValuePair<string, string>> Auth() =>
        new[] { KeyValuePair.Create("Authorization", "Bearer " + _apiKey) };

    // ------------------------------------------------------------------ chat
    public async Task<AssistantMessage> ChatAsync(IReadOnlyList<JsonObject> messages, JsonArray? tools,
                                                  Action<string>? onText, CancellationToken ct)
    {
        var model = Model;
        var triedSibling = false;
        for (var attempt = 1; ; attempt++)
        {
            var emitted = false;
            void Emit(string text) { emitted = true; onText?.Invoke(text); }

            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["stream"] = true,
            };
            if (!_omitTemperature)
                payload["temperature"] = Temperature;
            if (tools is { Count: > 0 })
            {
                payload["tools"] = tools.DeepClone();
                payload["tool_choice"] = "auto";
            }
            if (!_isGroq)
                payload["stream_options"] = new JsonObject { ["include_usage"] = true };

            using var request = Http.Json(HttpMethod.Post, _baseUrl + "/chat/completions", payload, Auth());
            using var response = await Http.SendAsync(request, true, Deadline, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var wait = RetryAfter(response);
                var error = await Http.ErrorText(response).ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    // out of tokens for this minute: the sibling model has its own allowance
                    if (!triedSibling && Sibling is { } sibling)
                    {
                        triedSibling = true;
                        model = sibling;
                        attempt--;
                        continue;
                    }
                    throw new RateLimitException((_isGroq ? "Groq" : "The provider") + " rate limit reached.", wait ?? ParseWait(error));
                }
                // reasoning models refuse a temperature; ask again without one
                if ((int)response.StatusCode == 400 && error.Contains("temperature", StringComparison.OrdinalIgnoreCase)
                    && !_omitTemperature)
                {
                    _omitTemperature = true;
                    attempt--;
                    continue;
                }
                if (Http.Retryable(response.StatusCode) && attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw Translate(error);
            }

            try
            {
                return await ReadStream(response, Emit, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !emitted && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    async Task<AssistantMessage> ReadStream(HttpResponseMessage response, Action<string> emit, CancellationToken ct)
    {
        var result = new AssistantMessage();
        var text = new System.Text.StringBuilder();
        var building = new SortedDictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
        var think = new ThinkFilter();

        await foreach (var data in Http.ReadEvents(response, ct).ConfigureAwait(false))
        {
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException) { continue; }
            if (chunk is null) continue;

            var usage = chunk["usage"] as JsonObject ?? chunk["x_groq"]?["usage"] as JsonObject;
            if (usage is not null)
            {
                result.PromptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? 0;
                result.CompletionTokens = usage["completion_tokens"]?.GetValue<int>() ?? 0;
            }

            if (chunk["choices"] is not JsonArray { Count: > 0 } choices)
                continue;
            var choice = choices[0]!;
            if (choice["finish_reason"] is JsonValue finish && finish.TryGetValue<string>(out var reason))
                result.FinishReason = reason;
            if (choice["delta"] is not JsonObject delta)
                continue;

            if (delta["content"] is JsonValue content && content.TryGetValue<string>(out var piece) && piece.Length > 0)
            {
                var visible = think.Push(piece);
                if (visible.Length > 0)
                {
                    text.Append(visible);
                    emit(visible);
                }
            }

            if (delta["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls.OfType<JsonObject>())
                {
                    var index = call["index"]?.GetValue<int>() ?? 0;
                    if (!building.TryGetValue(index, out var slot))
                        slot = ("", "", new System.Text.StringBuilder());
                    if (call["id"] is JsonValue id && id.TryGetValue<string>(out var idText) && idText.Length > 0)
                        slot.Id = idText;
                    if (call["function"] is JsonObject function)
                    {
                        if (function["name"] is JsonValue name && name.TryGetValue<string>(out var nameText) && nameText.Length > 0)
                            slot.Name = nameText;
                        if (function["arguments"] is JsonValue args && args.TryGetValue<string>(out var argText))
                            slot.Args.Append(argText);
                    }
                    building[index] = slot;
                }
            }
        }

        var tail = think.Flush();
        if (tail.Length > 0) { text.Append(tail); emit(tail); }

        result.Content = text.ToString();
        foreach (var (index, slot) in building)
            if (slot.Name.Length > 0)
                result.ToolCalls.Add(ToolCall.FromRaw(slot.Id.Length > 0 ? slot.Id : "call_" + index, slot.Name, slot.Args.ToString()));
        Interlocked.Add(ref _tokens, result.PromptTokens + result.CompletionTokens);
        return result;
    }

    // ---------------------------------------------------------------- vision
    public async Task<string> VisionAsync(string prompt, byte[] jpeg, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = VisionModel,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = prompt },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) },
                    }),
            }),
        };
        if (!_omitTemperature)
            payload["temperature"] = 0.2;

        using var request = Http.Json(HttpMethod.Post, _baseUrl + "/chat/completions", payload, Auth());
        using var response = await Http.SendAsync(request, false, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ThinkFilter.Strip(body?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "");
    }

    // ------------------------------------------------------------ transcribe
    public async Task<Transcript> TranscribeAsync(byte[] wav, string language, CancellationToken ct)
    {
        var fields = new List<(string, string)>
        {
            ("model", SpeechModel),
            ("response_format", "verbose_json"),
            ("temperature", "0"),
            ("language", language),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/audio/transcriptions")
        {
            Content = Http.Form(wav, "speech.wav", "audio/wav", fields),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return new Transcript(
            (body?["text"]?.GetValue<string>() ?? "").Trim(),
            body?["language"]?.GetValue<string>() ?? language);
    }

    // ----------------------------------------------------------------- speak
    public async Task<byte[]> SpeakAsync(string text, string model, string voice, string style, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = voice,
            ["response_format"] = "wav",
        };
        if (!string.IsNullOrWhiteSpace(style))
            payload["instructions"] = style;

        using var request = Http.Json(HttpMethod.Post, _baseUrl + "/audio/speech", payload, Auth());
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ meta
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var request = Http.Json(HttpMethod.Get, _baseUrl + "/models", null, Auth());
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return (body?["data"] as JsonArray ?? new JsonArray())
            .Select(m => m?["id"]?.GetValue<string>() ?? "")
            .Where(id => id.Length > 0 && !id.StartsWith("whisper") && !id.StartsWith("distil-whisper")
                         && !id.Contains("tts") && !id.Contains("guard"))
            .OrderBy(id => id)
            .ToList();
    }

    static readonly HashSet<string> NoToolModels = new() { "groq/compound", "groq/compound-mini", "allam-2-7b", "gpt-3.5-turbo-instruct" };

    public bool SupportsTools(string model) => !NoToolModels.Contains(model.ToLowerInvariant());

    static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.TryGetValues("retry-after", out var values)
            && double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>"Please try again in 7.83s" / "in 1m2.5s".</summary>
    static TimeSpan? ParseWait(string error)
    {
        var match = System.Text.RegularExpressions.Regex.Match(error, @"try again in (?:(\d+)m)?([\d.]+)s");
        if (!match.Success) return null;
        var minutes = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
        return TimeSpan.FromSeconds(minutes * 60 + double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    ProviderException Translate(string error)
    {
        var lowered = error.ToLowerInvariant();
        var keys = _isGroq ? "https://console.groq.com/keys" : "https://platform.openai.com/api-keys";
        if (error.StartsWith("401") || lowered.Contains("invalid api key") || lowered.Contains("unauthorized"))
            return new AuthException("That key was not accepted. Check it at " + keys);
        if (error.StartsWith("429") || lowered.Contains("rate limit"))
            return new RateLimitException((_isGroq ? "Groq" : "The provider") + " rate limit reached. Wait a moment and try again.");
        if (lowered.Contains("quota") || lowered.Contains("billing") || lowered.Contains("credit"))
            return new ProviderException("That key has no credit left. Top it up, or switch to the free provider.");
        if ((error.StartsWith("404") && lowered.Contains("model")) || lowered.Contains("model_not_found") || lowered.Contains("does not exist"))
            return new ProviderException("Model '" + Model + "' is not available to this key. Pick another one from the model list.");
        if (lowered.Contains("tool") && lowered.Contains("not supported"))
            return new ProviderException("'" + Model + "' cannot call tools, and Vigil cannot work without them. Pick another model.");
        return new ProviderException((_isGroq ? "Groq: " : "OpenAI: ") + error);
    }
}
