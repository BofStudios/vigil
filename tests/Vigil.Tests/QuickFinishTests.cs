using System.Text.Json.Nodes;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>"Open Paint" should cost one trip to the model, not two: a plain action that worked ends the
/// turn in its own words. Anything else - a failure, two actions, a follow-up - still goes back to the model.</summary>
[Collection("mind")]
public class QuickFinishTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vigil-finish-" + Guid.NewGuid().ToString("N")[..8]);

    public QuickFinishTests() => Mind.UseRoot(_root);

    public void Dispose()
    {
        Mind.UseRoot("");
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A model that says what it is told to, in order, and counts how often it was asked.</summary>
    sealed class Scripted(params AssistantMessage[] replies) : IProvider
    {
        int _next;
        public int Calls;
        public string Name => "scripted";
        public string Model { get; set; } = "scripted";
        public long TotalTokens => 0;
        public Task<AssistantMessage> ChatAsync(IReadOnlyList<JsonObject> messages, JsonArray? tools, Action<string>? onText, CancellationToken ct)
        {
            Calls++;
            var reply = replies[Math.Min(_next++, replies.Length - 1)];
            if (reply.Content.Length > 0) onText?.Invoke(reply.Content);
            return Task.FromResult(reply);
        }
        public Task<string> VisionAsync(string prompt, byte[] jpeg, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public bool SupportsTools(string model) => true;
    }

    sealed class Host : IAgentHost
    {
        public string Streamed = "";
        public void StreamText(string chunk) => Streamed += chunk;
        public void EndStream() { }
        public void ToolStarted(string tool, string summary, Risk risk) { }
        public void ToolFinished(string tool, string output, bool ok) { }
        public void Note(string text) { }
        public void Warn(string text) { }
        public void Error(string text) { }
        public void PlanChanged(IReadOnlyList<PlanStep> steps) { }
        public string? AskUser(string question, IReadOnlyList<string> choices, CancellationToken ct) => null;
        public Answer Confirm(GuardAction action) => Answer.Yes;
        public IDisposable Driving() => new Nothing();
        public IDisposable OutOfShot() => new Nothing();
        sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    static AssistantMessage Call(string tool, string args, string id = "c1")
    {
        var message = new AssistantMessage();
        message.ToolCalls.Add(ToolCall.FromRaw(id, tool, args));
        return message;
    }

    static AssistantMessage Say(string text) => new() { Content = text };

    static Registry Tools(bool fails = false)
    {
        var registry = new Registry();
        registry.Add(new[]
        {
            new ToolSpec
            {
                Name = "launch", Description = "test", Parameters = new JsonObject(), Risk = Risk.Safe,
                Handler = (_, a) => fails ? throw new ToolError("not found") : a.Str("target") + " opened.",
                Finish = (a, _) => a.Str("target") + " is open.",
            },
        });
        return registry;
    }

    [Fact]
    public async Task One_plain_action_that_worked_is_one_trip_to_the_model()
    {
        var model = new Scripted(Call("launch", "{\"target\":\"Paint\"}"), Say("should not be needed"));
        var host = new Host();
        var agent = new Agent(new Config { ApprovalMode = "yolo" }, model, Tools(), host);

        var answer = await agent.RunAsync("Open Paint");

        Assert.Equal("Paint is open.", answer);
        Assert.Equal(1, model.Calls);
        Assert.Contains("Paint is open.", host.Streamed);
    }

    [Fact]
    public async Task A_failed_action_goes_back_to_the_model()
    {
        var model = new Scripted(Call("launch", "{\"target\":\"Paint\"}"), Say("I could not find Paint."));
        var agent = new Agent(new Config { ApprovalMode = "yolo" }, model, Tools(fails: true), new Host());

        var answer = await agent.RunAsync("Open Paint");

        Assert.Equal("I could not find Paint.", answer);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task Two_actions_at_once_go_back_to_the_model()
    {
        var both = Call("launch", "{\"target\":\"Paint\"}");
        both.ToolCalls.Add(ToolCall.FromRaw("c2", "launch", "{\"target\":\"Notepad\"}"));
        var model = new Scripted(both, Say("Both are open."));
        var agent = new Agent(new Config { ApprovalMode = "yolo" }, model, Tools(), new Host());

        Assert.Equal("Both are open.", await agent.RunAsync("Open Paint and Notepad"));
        Assert.Equal(2, model.Calls);
    }

    [Theory]
    [InlineData("Madonna - Like A Prayer (Official Video)", "Madonna - Like A Prayer")]
    [InlineData("Kenan Doğulu - Haddini Bileceksin [Official Music Video]", "Kenan Doğulu - Haddini Bileceksin")]
    [InlineData("Lo-fi beats | study music", "Lo-fi beats")]
    [InlineData("Song &amp; Dance", "Song & Dance")]
    public void A_video_title_is_said_the_way_a_person_would(string title, string spoken) =>
        Assert.Equal(spoken, MediaTools.Speakable(title));
}
