using System.Diagnostics;
using System.Text;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>How long ordinary requests really take, start to answer, on the real key - written to a
/// report rather than asserted, because the network decides part of it. VIGIL_LIVE=1 only.</summary>
[Collection("mind")]
public class SpeedTests
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
    public async Task How_long_ordinary_requests_take()
    {
        if (Environment.GetEnvironmentVariable("VIGIL_LIVE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "vigil-speed-mind");
        Mind.UseRoot(root);
        try
        {
            var report = new StringBuilder();
            foreach (var (input, spoken) in new[] { ("Hello?", "english"), ("Open Paint", "english"), ("What time is it?", (string?)null), ("Madonna'dan prayer şarkısını aç", "turkish") })
            {
                var config = Config.Load();
                config.ApprovalMode = "ask";
                var host = new Host();
                var agent = new Agent(config, ProviderFactory.Build(config), Registry.Build(config), host);
                var watch = Stopwatch.StartNew();
                var answer = await agent.RunAsync(input, spokenLanguage: spoken);
                report.AppendLine($"{watch.Elapsed.TotalSeconds:0.0}s  {agent.StepsUsed} step(s)  tokens {agent.Provider.TotalTokens}  [{input}]");
                lock (host.Lines) foreach (var line in host.Lines) report.AppendLine(line);
                report.AppendLine("  -> " + answer.Replace("\n", " "));
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-speed.txt"), report.ToString());
        }
        finally
        {
            Mind.UseRoot("");
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
