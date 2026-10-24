using System.Diagnostics;
using System.Text;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>What a conversation costs as it goes on: the free tier gives 8,000 tokens a minute per model,
/// so every turn that carries the whole history makes the next one wait. Several turns on one agent,
/// tokens and seconds per turn, written to a report. VIGIL_LIVE=1 only.</summary>
[Collection("mind")]
public class ConversationCostTests
{
    sealed class Host : IAgentHost
    {
        public readonly List<string> Lines = new();
        public void StreamText(string chunk) { }
        public void EndStream() { }
        public void ToolStarted(string tool, string summary, Risk risk) { lock (Lines) Lines.Add("  tool " + summary); }
        public void ToolFinished(string tool, string output, bool ok) { }
        public void Note(string text) { lock (Lines) Lines.Add("  note " + text); }
        public void Warn(string text) { lock (Lines) Lines.Add("  warn " + text); }
        public void Error(string text) { lock (Lines) Lines.Add("  error " + text); }
        public void PlanChanged(IReadOnlyList<PlanStep> steps) { }
        public string? AskUser(string question, IReadOnlyList<string> choices, CancellationToken ct) => null;
        public Answer Confirm(GuardAction action) => Answer.No;   // nothing actually opens during the test
        public IDisposable Driving() => new Nothing();
        public IDisposable OutOfShot() => new Nothing();
        sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task A_long_conversation_stays_cheap()
    {
        if (Environment.GetEnvironmentVariable("VIGIL_LIVE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "vigil-cost-mind");
        Mind.UseRoot(root);
        try
        {
            var config = Config.Load();
            config.ApprovalMode = "ask";
            var host = new Host();
            var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);
            var report = new StringBuilder();
            var before = 0L;
            foreach (var (input, spoken) in new[]
                     {
                         ("What's in my Downloads folder?", (string?)null),
                         ("Which of those is the biggest?", null),
                         ("What time is it?", "english"),
                         ("Open Paint", "english"),
                         ("How much free space is on C?", null),
                         ("Thanks. What did I ask first?", null),
                     })
            {
                var watch = Stopwatch.StartNew();
                var answer = await agent.RunAsync(input, spokenLanguage: spoken);
                var spent = agent.Provider.TotalTokens - before;
                before = agent.Provider.TotalTokens;
                report.AppendLine($"{watch.Elapsed.TotalSeconds:0.0}s  {agent.StepsUsed} step(s)  {spent} tokens  history {agent.Messages.Count} msgs  [{input}]");
                lock (host.Lines)
                {
                    foreach (var line in host.Lines) report.AppendLine(line);
                    host.Lines.Clear();
                }
                report.AppendLine("  -> " + Text.Clip(answer.Replace("\n", " "), 200));
            }
            report.AppendLine("total " + agent.Provider.TotalTokens + " tokens");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-cost.txt"), report.ToString());
        }
        finally
        {
            Mind.UseRoot("");
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
