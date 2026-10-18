using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vigil.Core.Providers;

/// <summary>Fully local: no key, no account, no network beyond this machine.</summary>
public sealed class OllamaProvider : IProvider
{
    static readonly string[] ToolHints = { "qwen", "llama3.1", "llama3.2", "llama3.3", "mistral", "firefunction", "command-r", "hermes", "gpt-oss", "granite" };

    readonly string _host;
    long _tokens;

    public string Name => "ollama";
    public string Model { get; set; }
    public string VisionModel { get; set; }
    public double Temperature { get; set; }
    public long TotalTokens => Interlocked.Read(ref _tokens);

    public OllamaProvider(Config c)
    {
        _host = (string.IsNullOrWhiteSpace(c.OllamaHost) ? Config.DefaultOllamaHost : c.OllamaHost).TrimEnd('/');
        Model = string.IsNullOrWhiteSpace(c.OllamaModel) ? Config.DefaultOllamaModel : c.OllamaModel;
        VisionModel = string.IsNullOrWhiteSpace(c.OllamaVisionModel) ? Config.DefaultOllamaVisionModel : c.OllamaVisionModel;
        Temperature = c.Temperature;
    }

    /// <summary>OpenAI-shaped history in Ollama's format.</summary>
    public static JsonArray ToOllama(IReadOnlyList<JsonObject> messages)
    {
        var converted = new JsonArray();
        foreach (var message in messages)
        {
            var role = Msg.Role(message);
            if (role == "tool")
            {
                converted.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["content"] = Msg.Text(message),
                    ["tool_name"] = message["name"]?.GetValue<string>() ?? "",
                });
                continue;
            }
            var item = new JsonObject { ["role"] = role, ["content"] = Msg.Text(message) };
            if (message["tool_calls"] is JsonArray calls && calls.Count > 0)
            {
                var list = new JsonArray();
                foreach (var call in calls.OfType<JsonObject>())
                {
                    var raw = call["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                    JsonNode arguments;
                    try { arguments = JsonNode.Parse(raw) ?? new JsonObject(); }
                    catch (JsonException) { arguments = new JsonObject(); }
                    list.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = call["function"]?["name"]?.GetValue<string>() ?? "",
                            ["arguments"] = arguments,
                        },
                    });
                }
                item["tool_calls"] = list;
            }
            converted.Add(item);
        }
        return converted;
    }

    public async Task<AssistantMessage> ChatAsync(IReadOnlyList<JsonObject> messages, JsonArray? tools,
                                                  Action<string>? onText, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = ToOllama(messages),
            ["stream"] = true,
            ["options"] = new JsonObject { ["temperature"] = Temperature },
        };
        if (tools is { Count: > 0 })
            payload["tools"] = tools.DeepClone();

        using var request = Http.Json(HttpMethod.Post, _host + "/api/chat", payload);
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, true, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false); }
        catch (ProviderException) { throw Unreachable(); }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw Translate(await Http.ErrorText(response).ConfigureAwait(false));

            var result = new AssistantMessage { FinishReason = "stop" };
            var text = new StringBuilder();
            var think = new ThinkFilter();
            await foreach (var line in Http.ReadLines(response, ct).ConfigureAwait(false))
            {
                JsonNode? chunk;
                try { chunk = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                var message = chunk?["message"];
                var piece = message?["content"]?.GetValue<string>() ?? "";
                if (piece.Length > 0)
                {
                    var visible = think.Push(piece);
                    if (visible.Length > 0) { text.Append(visible); onText?.Invoke(visible); }
                }
                if (message?["tool_calls"] is JsonArray calls)
                {
                    foreach (var call in calls.OfType<JsonObject>())
                    {
                        var function = call["function"] as JsonObject;
                        var args = function?["arguments"];
                        var name = function?["name"]?.GetValue<string>() ?? "";
                        var id = call["id"]?.GetValue<string>() ?? "ollama_call_" + result.ToolCalls.Count;
                        result.ToolCalls.Add(args is JsonObject obj
                            ? ToolCall.FromObject(id, name, (JsonObject)obj.DeepClone())
                            : ToolCall.FromRaw(id, name, args?.ToString()));
                    }
                }
                if (chunk?["done"]?.GetValue<bool>() == true)
                {
                    result.PromptTokens = chunk["prompt_eval_count"]?.GetValue<int>() ?? 0;
                    result.CompletionTokens = chunk["eval_count"]?.GetValue<int>() ?? 0;
                }
            }
            var tail = think.Flush();
            if (tail.Length > 0) { text.Append(tail); onText?.Invoke(tail); }
            result.Content = text.ToString();
            Interlocked.Add(ref _tokens, result.PromptTokens + result.CompletionTokens);
            return result;
        }
    }

    public async Task<string> VisionAsync(string prompt, byte[] jpeg, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = VisionModel,
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0.2 },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = prompt,
                ["images"] = new JsonArray(Convert.ToBase64String(jpeg)),
            }),
        };
        using var request = Http.Json(HttpMethod.Post, _host + "/api/chat", payload);
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, false, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
        catch (ProviderException) { throw Unreachable(); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ThinkFilter.Strip(body?["message"]?["content"]?.GetValue<string>() ?? "");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var request = Http.Json(HttpMethod.Get, _host + "/api/tags", null);
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (ProviderException) { throw Unreachable(); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw Translate(await Http.ErrorText(response).ConfigureAwait(false));
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return (body?["models"] as JsonArray ?? new JsonArray())
                .Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).OrderBy(n => n).ToList();
        }
    }

    public bool SupportsTools(string model) => ToolHints.Any(h => model.Contains(h, StringComparison.OrdinalIgnoreCase));

    ProviderException Unreachable() => new(
        "Could not reach Ollama at " + _host + ". Install it from https://ollama.com/download, " +
        "make sure it is running, then pull a model: ollama pull " + Config.DefaultOllamaModel);

    ProviderException Translate(string error)
    {
        var lowered = error.ToLowerInvariant();
        if (lowered.Contains("not found") || error.StartsWith("404"))
            return new ProviderException("Model not found: " + Model + ". Pull it with: ollama pull " + Model);
        if (lowered.Contains("does not support tools"))
            return new ProviderException("This model cannot call tools. Try a tool-capable one: ollama pull qwen3:8b");
        return new ProviderException("Ollama: " + error);
    }
}
