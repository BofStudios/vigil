using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vigil.Core.Providers;

/// <summary>The conversation is kept in OpenAI's shape everywhere in Vigil - plain
/// JSON objects with role/content/tool_calls - because three of the four
/// providers speak it. Claude's shape is translated at its own door.</summary>
public static class Msg
{
    public static JsonObject System(string text) => new() { ["role"] = "system", ["content"] = text };
    public static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };

    public static JsonObject ToolResult(string callId, string name, string content) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = callId,
        ["name"] = name,
        ["content"] = content,
    };

    public static string Role(JsonObject message) => message["role"]?.GetValue<string>() ?? "";

    public static string Text(JsonObject message)
    {
        var content = message["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        if (content is JsonArray parts)
            return string.Concat(parts.OfType<JsonObject>()
                .Where(p => p["type"]?.GetValue<string>() == "text")
                .Select(p => p["text"]?.GetValue<string>() ?? ""));
        return "";
    }

    public static bool HasToolCalls(JsonObject message) =>
        message["tool_calls"] is JsonArray calls && calls.Count > 0;
}

public sealed record ToolCall(string Id, string Name, JsonObject Arguments, string RawArguments)
{
    public static ToolCall FromRaw(string id, string name, string? raw)
    {
        JsonObject parsed = new();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var node = JsonNode.Parse(raw);
                parsed = node as JsonObject ?? new JsonObject { ["value"] = node };
            }
            catch (JsonException) { }
        }
        return new ToolCall(id, name, parsed, raw ?? "");
    }

    public static ToolCall FromObject(string id, string name, JsonObject arguments) =>
        new(id, name, arguments, arguments.ToJsonString());
}

public sealed class AssistantMessage
{
    public string Content { get; set; } = "";
    public List<ToolCall> ToolCalls { get; } = new();
    public string FinishReason { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    public bool WantsTools => ToolCalls.Count > 0;

    /// <summary>The OpenAI-shaped message to append to the conversation.</summary>
    public JsonObject ToMessage()
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = Content };
        if (ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var call in ToolCalls)
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = string.IsNullOrEmpty(call.RawArguments) ? "{}" : call.RawArguments,
                    },
                });
            message["tool_calls"] = calls;
        }
        return message;
    }
}

public class ProviderException(string message) : Exception(message);
public sealed class AuthException(string message) : ProviderException(message);
/// <summary>The provider's quota for the minute is used up. RetryAfter is how long it asked us to wait.</summary>
public sealed class RateLimitException(string message, TimeSpan? retryAfter = null) : ProviderException(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter ?? TimeSpan.FromSeconds(8);
}
/// <summary>The request on its own is more than the provider takes in a minute: waiting will not help,
/// only a shorter one will.</summary>
public sealed class TooLargeException(string message) : ProviderException(message);

public sealed record Transcript(string Text, string Language);

/// <summary>What every brain Vigil can think with has to offer.</summary>
public interface IProvider
{
    string Name { get; }
    string Model { get; set; }
    long TotalTokens { get; }

    Task<AssistantMessage> ChatAsync(IReadOnlyList<JsonObject> messages, JsonArray? tools,
                                     Action<string>? onText, CancellationToken ct);

    /// <summary>Look at a JPEG and answer a question about it.</summary>
    Task<string> VisionAsync(string prompt, byte[] jpeg, CancellationToken ct);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

    bool SupportsTools(string model);
}

/// <summary>Some providers can also hear.</summary>
public interface ITranscriber
{
    /// <summary>`quick`: a faster, rougher pass - for the live words while someone is still talking.</summary>
    Task<Transcript> TranscribeAsync(byte[] wav, string language, CancellationToken ct, bool quick = false);
}

/// <summary>...and some can speak.</summary>
public interface ISpeechSynthesizer
{
    Task<byte[]> SpeakAsync(string text, string model, string voice, string style, CancellationToken ct);
}
