using System.Text.Json.Nodes;
using Vigil.Core.Security;

namespace Vigil.Core.Tools;

/// <summary>What the agent tells the window about. Implemented by the window; the
/// engine never touches the interface directly.</summary>
public interface IAgentHost
{
    void StreamText(string chunk);
    void EndStream();
    void ToolStarted(string tool, string summary, Risk risk);
    void ToolFinished(string tool, string output, bool ok);
    void Note(string text);
    void Warn(string text);
    void Error(string text);
    void PlanChanged(IReadOnlyList<PlanStep> steps);

    /// <summary>Ask the person something and wait for the answer, spoken or typed.
    /// Returns null when they dismissed it.</summary>
    string? AskUser(string question, IReadOnlyList<string> choices, CancellationToken ct);

    /// <summary>Asks for approval. Blocks the (worker) thread until the person answers.</summary>
    Answer Confirm(GuardAction action);

    /// <summary>The screen edge lights while Vigil holds the pointer or keyboard.</summary>
    IDisposable Driving();

    /// <summary>Keeps Vigil's own window out of a screenshot it is about to take.</summary>
    IDisposable OutOfShot();
}

public sealed record PlanStep(string Text, string Status, string Note = "");

public sealed class Registry
{
    readonly Dictionary<string, ToolSpec> _tools = new();

    public void Register(ToolSpec spec) => _tools[spec.Name] = spec;

    public void Add(IEnumerable<ToolSpec> specs)
    {
        foreach (var spec in specs) Register(spec);
    }

    public ToolSpec? Get(string name) => _tools.GetValueOrDefault(name);
    public IReadOnlyList<ToolSpec> Specs => _tools.Values.ToList();
    public IReadOnlyList<string> Names => _tools.Keys.OrderBy(n => n).ToList();
    public int Count => _tools.Count;

    public IReadOnlyDictionary<string, List<ToolSpec>> Groups() =>
        _tools.Values.GroupBy(s => s.Group).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Name).ToList());

    public static JsonArray Schemas(IEnumerable<ToolSpec> specs) => new(specs.Select(s => (JsonNode)s.Schema()).ToArray());

    /// <summary>Every tool this configuration allows, grouped the way the model sees them.</summary>
    public static Registry Build(Config config)
    {
        var registry = new Registry();
        registry.Add(Compact.Build(config));
        return registry;
    }
}
