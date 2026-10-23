using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>Where the tokens of every request go: the free tier gives 8,000 a minute per model, and every
/// request carries the whole system prompt and every tool description. Written to a report.</summary>
[Collection("mind")]
public class TokenReportTests
{
    sealed class Host : IAgentHost
    {
        public void StreamText(string chunk) { }
        public void EndStream() { }
        public void ToolStarted(string tool, string summary, Risk risk) { }
        public void ToolFinished(string tool, string output, bool ok) { }
        public void Note(string text) { }
        public void Warn(string text) { }
        public void Error(string text) { }
        public void PlanChanged(IReadOnlyList<PlanStep> steps) { }
        public string? AskUser(string question, IReadOnlyList<string> choices, CancellationToken ct) => null;
        public Answer Confirm(GuardAction action) => Answer.No;
        public IDisposable Driving() => new Nothing();
        public IDisposable OutOfShot() => new Nothing();
        sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    static int Tokens(string text) => text.Length / 4;

    [Fact]
    public void Where_the_tokens_go()
    {
        var config = new Config { ApiKey = "x" };
        var registry = Registry.Build(config);
        var agent = new Agent(config, ProviderFactory.Build(config), registry, new Host());
        var system = Msg.Text(agent.Messages[0]);
        var schemas = Registry.Schemas(registry.Specs.Where(s => s.Group != "planning"));
        var lines = new List<string>
        {
            "system prompt: " + Tokens(system),
            "tool schemas: " + Tokens(schemas.ToJsonString()) + " (" + schemas.Count + " tools)",
        };
        lines.AddRange(registry.Specs.Select(s => "  " + s.Name + " " + Tokens(s.Schema().ToJsonString()))
                               .OrderByDescending(l => int.Parse(l.Split(' ').Last())));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "vigil-tokens.txt"), lines);
    }
}
