using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vigil.Core.Providers;
using Vigil.Core.Security;

namespace Vigil.Core.Tools;

/// <summary>A failure the model can read and act on.</summary>
public class ToolError(string message) : Exception(message);

/// <summary>The person, or the rules, said no.</summary>
public sealed class PermissionDenied(string message) : ToolError(message);

/// <summary>The arguments the model sent, read forgivingly: models send "5" for 5,
/// "true" for true, and a single string where a list was asked for.</summary>
public sealed class ToolArgs(JsonObject values)
{
    public JsonObject Values { get; } = values;

    public bool Has(string name) => Values[name] is not null;

    public string Str(string name, string fallback = "")
    {
        var node = Values[name];
        if (node is null) return fallback;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            return value.ToJsonString();
        }
        return node.ToJsonString();
    }

    public string Req(string name)
    {
        if (!Has(name))
            throw new ToolError("Missing required parameter '" + name + "'.");
        return Str(name);
    }

    public int Int(string name, int fallback = 0)
    {
        var node = Values[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number)) return number;
            if (value.TryGetValue<double>(out var real)) return (int)Math.Round(real);
            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return (int)Math.Round(parsed);
        }
        return fallback;
    }

    public double Num(string name, double fallback = 0)
    {
        var node = Values[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var real)) return real;
            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return fallback;
    }

    public bool Bool(string name, bool fallback = false)
    {
        var node = Values[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag)) return flag;
            if (value.TryGetValue<string>(out var text))
                return text.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "on";
            if (value.TryGetValue<int>(out var number)) return number != 0;
        }
        return fallback;
    }

    public List<string> List(string name)
    {
        var node = Values[name];
        if (node is JsonArray array)
            return array.Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : item?.ToJsonString() ?? "").ToList();
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            // a JSON list sent as a string, or one item on its own
            try
            {
                if (JsonNode.Parse(text) is JsonArray parsed)
                    return new ToolArgs(new JsonObject { ["x"] = parsed.DeepClone() }).List("x");
            }
            catch (JsonException) { }
            return new List<string> { text };
        }
        return new List<string>();
    }

    public List<int> Ints(string name) =>
        List(name).Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToList();
}

public sealed class ToolSpec
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
    public required Func<ToolContext, ToolArgs, string> Handler { get; init; }
    public string Group { get; init; } = "core";
    public Risk Risk { get; init; } = Risk.Moderate;
    public Func<ToolArgs, Risk>? RiskFn { get; init; }
    public Func<ToolArgs, string>? Preview { get; init; }
    /// <summary>The tool draws its own result (a plan, a question); skip the echo.</summary>
    public bool QuietResult { get; init; }
    /// <summary>The tool takes the pointer or keyboard: light the screen edge while it runs.</summary>
    public bool Drives { get; init; }

    public Risk RiskFor(ToolArgs args)
    {
        if (RiskFn is null) return Risk;
        try { return RiskFn(args); }
        catch (Exception) { return Risk; }
    }

    public string Summarize(ToolArgs args)
    {
        if (Preview is not null)
        {
            try { return Preview(args); }
            catch (Exception) { }
        }
        if (args.Values.Count == 0) return Name;
        var parts = args.Values.Select(p =>
        {
            var text = p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : p.Value?.ToJsonString() ?? "";
            return p.Key + "=" + (text.Length > 80 ? text[..77] + "..." : text);
        });
        return Name + "(" + string.Join(", ", parts) + ")";
    }

    public IReadOnlyCollection<string> Allowed =>
        (Parameters["properties"] as JsonObject)?.Select(p => p.Key).ToList() ?? new List<string>();

    public JsonObject Schema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters.DeepClone(),
        },
    };
}

/// <summary>Builds the JSON schema for a tool's parameters without a wall of braces.</summary>
public static class Schema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, isRequired) in properties)
        {
            props[name] = schema;
            if (isRequired) required.Add(name);
        }
        var result = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    public static JsonObject Str(string description = "", params string[] choices)
    {
        var node = new JsonObject { ["type"] = "string" };
        if (description.Length > 0) node["description"] = description;
        if (choices.Length > 0) node["enum"] = new JsonArray(choices.Select(c => (JsonNode)c).ToArray());
        return node;
    }

    public static JsonObject Int(string description = "") =>
        description.Length > 0 ? new() { ["type"] = "integer", ["description"] = description } : new() { ["type"] = "integer" };

    public static JsonObject Num(string description = "") =>
        description.Length > 0 ? new() { ["type"] = "number", ["description"] = description } : new() { ["type"] = "number" };

    public static JsonObject Bool(string description = "") =>
        description.Length > 0 ? new() { ["type"] = "boolean", ["description"] = description } : new() { ["type"] = "boolean" };

    public static JsonObject List(string itemType, string description = "")
    {
        var node = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = itemType } };
        if (description.Length > 0) node["description"] = description;
        return node;
    }

    public static (string, JsonObject, bool) P(string name, JsonObject schema, bool required = false) => (name, schema, required);
}

/// <summary>What a tool can reach while it runs.</summary>
public sealed class ToolContext
{
    public required Config Config { get; init; }
    public required Guard Guard { get; init; }
    public required IProvider Provider { get; set; }
    public required IAgentHost Host { get; init; }
    public string Cwd { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public Dictionary<string, object> State { get; } = new();
    public CancellationToken Cancel { get; set; }

    /// <summary>Relative paths are taken from the working directory.</summary>
    public string Resolve(string path)
    {
        var expanded = Rules.ExpandHome(path.Trim().Trim('"'));
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(Cwd, expanded));
    }

    /// <summary>Runs async work from a synchronous tool. Tools run on a worker thread,
    /// never the window's, so waiting here cannot deadlock the interface.</summary>
    public T Wait<T>(Func<CancellationToken, Task<T>> work) =>
        Task.Run(() => work(Cancel)).GetAwaiter().GetResult();
}

public static class Text
{
    /// <summary>Shorten long output so it does not blow up the model's context.</summary>
    public static string Truncate(string? text, int limit = 12000, string note = "output truncated")
    {
        text ??= "";
        if (text.Length <= limit) return text;
        var head = text[..(int)(limit * 0.7)];
        var tail = text[^(int)(limit * 0.25)..];
        var removed = text.Length - head.Length - tail.Length;
        return head + "\n\n... [" + note + ": " + removed + " characters skipped] ...\n\n" + tail;
    }

    public static string Human(double size)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? ((long)size) + " B" : size.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 3)] + "...";
}
