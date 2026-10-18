using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vigil.Core.Providers;

/// <summary>Bring your own Claude.
///
/// Claude's Messages API is a different shape from the rest of Vigil: the system
/// prompt is a parameter, tool calls are content blocks, and tool results come
/// back as user messages. This class translates both ways and keeps that
/// translation in one place.</summary>
public sealed class AnthropicProvider : IProvider
{
    const string Url = "https://api.anthropic.com/v1";
    const int MaxTokens = 8192;
    static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    readonly string _apiKey;
    long _tokens;

    public string Name => "anthropic";
    public string Model { get; set; }
    public double Temperature { get; set; }
    public long TotalTokens => Interlocked.Read(ref _tokens);

    public AnthropicProvider(Config c)
    {
        if (string.IsNullOrWhiteSpace(c.AnthropicApiKey))
            throw new AuthException("No Anthropic key. Get one at https://console.anthropic.com/settings/keys");
        _apiKey = c.AnthropicApiKey;
        Model = string.IsNullOrWhiteSpace(c.AnthropicModel) ? Config.DefaultAnthropicModel : c.AnthropicModel;
        Temperature = c.Temperature;
    }

    IEnumerable<KeyValuePair<string, string>> Headers() => new[]
    {
        KeyValuePair.Create("x-api-key", _apiKey),
        KeyValuePair.Create("anthropic-version", "2023-06-01"),
    };

    // ----------------------------------------------------------- translation
    /// <summary>(system, messages) in Claude's shape. A run of tool results becomes one
    /// user message with several blocks, because Claude rejects a tool_result that does
    /// not answer the assistant turn immediately before it.</summary>
    public static (string System, JsonArray Messages) ToAnthropic(IReadOnlyList<JsonObject> messages)
    {
        var system = new StringBuilder();
        var converted = new JsonArray();
        var pending = new JsonArray();

        void Flush()
        {
            if (pending.Count == 0) return;
            converted.Add(new JsonObject { ["role"] = "user", ["content"] = pending });
            pending = new JsonArray();
        }

        foreach (var message in messages)
        {
            switch (Msg.Role(message))
            {
                case "system":
                    if (system.Length > 0) system.Append("\n\n");
                    system.Append(Msg.Text(message));
                    break;

                case "tool":
                    pending.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message["tool_call_id"]?.GetValue<string>() ?? "",
                        ["content"] = Msg.Text(message),
                    });
                    break;

                case "assistant":
                {
                    Flush();
                    var blocks = new JsonArray();
                    var text = Msg.Text(message);
                    if (text.Length > 0)
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    if (message["tool_calls"] is JsonArray calls)
                    {
                        foreach (var call in calls.OfType<JsonObject>())
                        {
                            var function = call["function"] as JsonObject;
                            var raw = function?["arguments"]?.GetValue<string>() ?? "{}";
                            JsonObject input;
                            try { input = JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw) as JsonObject ?? new JsonObject(); }
                            catch (JsonException) { input = new JsonObject(); }
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = call["id"]?.GetValue<string>() ?? "",
                                ["name"] = function?["name"]?.GetValue<string>() ?? "",
                                ["input"] = input,
                            });
                        }
                    }
                    if (blocks.Count > 0)
                        converted.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    break;
                }

                case "user":
                {
                    Flush();
                    var content = message["content"];
                    JsonArray blocks = content is JsonArray parts
                        ? (JsonArray)parts.DeepClone()
                        : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Msg.Text(message) });
                    converted.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
                    break;
                }
            }
        }
        Flush();
        return (system.ToString(), converted);
    }

    public static JsonArray ToAnthropicTools(JsonArray? tools)
    {
        var converted = new JsonArray();
        foreach (var tool in (tools ?? new JsonArray()).OfType<JsonObject>())
        {
            var function = tool["function"] as JsonObject;
            converted.Add(new JsonObject
            {
                ["name"] = function?["name"]?.GetValue<string>() ?? "",
                ["description"] = function?["description"]?.GetValue<string>() ?? "",
                ["input_schema"] = function?["parameters"]?.DeepClone()
                                   ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            });
        }
        return converted;
    }

    // ------------------------------------------------------------------ chat
    public async Task<AssistantMessage> ChatAsync(IReadOnlyList<JsonObject> messages, JsonArray? tools,
                                                  Action<string>? onText, CancellationToken ct)
    {
        var (system, converted) = ToAnthropic(messages);
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = MaxTokens,
            ["temperature"] = Temperature,
            ["messages"] = converted,
            ["stream"] = true,
        };
        if (system.Length > 0)
            payload["system"] = system;
        if (tools is { Count: > 0 })
            payload["tools"] = ToAnthropicTools(tools);

        for (var attempt = 1; ; attempt++)
        {
            using var request = Http.Json(HttpMethod.Post, Url + "/messages", payload.DeepClone(), Headers());
            using var response = await Http.SendAsync(request, true, Deadline, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await Http.ErrorText(response).ConfigureAwait(false);
                if (Http.Retryable(response.StatusCode) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw Translate(error);
            }
            return await ReadStream(response, onText, ct).ConfigureAwait(false);
        }
    }

    async Task<AssistantMessage> ReadStream(HttpResponseMessage response, Action<string>? onText, CancellationToken ct)
    {
        var result = new AssistantMessage();
        var text = new StringBuilder();
        var blocks = new Dictionary<int, (string Type, string Id, string Name, StringBuilder Json)>();

        await foreach (var data in Http.ReadEvents(response, ct).ConfigureAwait(false))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch (JsonException) { continue; }
            switch (node?["type"]?.GetValue<string>())
            {
                case "message_start":
                    result.PromptTokens = node["message"]?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
                    break;
                case "content_block_start":
                {
                    var index = node["index"]?.GetValue<int>() ?? 0;
                    var block = node["content_block"];
                    blocks[index] = (block?["type"]?.GetValue<string>() ?? "",
                                     block?["id"]?.GetValue<string>() ?? "",
                                     block?["name"]?.GetValue<string>() ?? "",
                                     new StringBuilder());
                    break;
                }
                case "content_block_delta":
                {
                    var index = node["index"]?.GetValue<int>() ?? 0;
                    var delta = node["delta"];
                    switch (delta?["type"]?.GetValue<string>())
                    {
                        case "text_delta":
                            var piece = delta["text"]?.GetValue<string>() ?? "";
                            text.Append(piece);
                            if (piece.Length > 0) onText?.Invoke(piece);
                            break;
                        case "input_json_delta":
                            if (blocks.TryGetValue(index, out var slot))
                                slot.Json.Append(delta["partial_json"]?.GetValue<string>() ?? "");
                            break;
                    }
                    break;
                }
                case "message_delta":
                    result.FinishReason = node["delta"]?["stop_reason"]?.GetValue<string>() ?? result.FinishReason;
                    result.CompletionTokens = node["usage"]?["output_tokens"]?.GetValue<int>() ?? result.CompletionTokens;
                    break;
                case "error":
                    throw Translate(node["error"]?["message"]?.GetValue<string>() ?? "stream error");
            }
        }

        result.Content = text.ToString();
        foreach (var (_, block) in blocks.OrderBy(b => b.Key))
            if (block.Type == "tool_use")
                result.ToolCalls.Add(ToolCall.FromRaw(block.Id, block.Name,
                    block.Json.Length == 0 ? "{}" : block.Json.ToString()));
        Interlocked.Add(ref _tokens, result.PromptTokens + result.CompletionTokens);
        return result;
    }

    // ---------------------------------------------------------------- vision
    public async Task<string> VisionAsync(string prompt, byte[] jpeg, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/jpeg",
                            ["data"] = Convert.ToBase64String(jpeg),
                        },
                    },
                    new JsonObject { ["type"] = "text", ["text"] = prompt }),
            }),
        };
        using var request = Http.Json(HttpMethod.Post, Url + "/messages", payload, Headers());
        using var response = await Http.SendAsync(request, false, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return string.Concat((body?["content"] as JsonArray ?? new JsonArray())
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b?["text"]?.GetValue<string>() ?? "")).Trim();
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var request = Http.Json(HttpMethod.Get, Url + "/models?limit=50", null, Headers());
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var found = (body?["data"] as JsonArray ?? new JsonArray())
            .Select(m => m?["id"]?.GetValue<string>() ?? "").Where(id => id.Length > 0).OrderBy(id => id).ToList();
        return found.Count > 0 ? found : Routes.ClaudeModels.Keys.OrderBy(k => k).ToList();
    }

    public bool SupportsTools(string model) => true;

    static ProviderException Translate(string error)
    {
        var lowered = error.ToLowerInvariant();
        if (lowered.Contains("authentication") || lowered.Contains("x-api-key") || error.StartsWith("401"))
            return new AuthException("Anthropic did not accept that key. Check it at https://console.anthropic.com/settings/keys");
        if (lowered.Contains("rate limit") || error.StartsWith("429"))
            return new RateLimitException("Anthropic rate limit reached. Wait a moment.");
        if (lowered.Contains("credit") || lowered.Contains("billing"))
            return new ProviderException("That Anthropic key has no credit left. Top it up, or switch to the free provider.");
        if (lowered.Contains("overloaded") || error.StartsWith("529"))
            return new ProviderException("Claude is overloaded right now. Try again in a moment.");
        return new ProviderException("Anthropic: " + error);
    }
}
