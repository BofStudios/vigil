using System.Text.Json.Nodes;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>Why a long conversation got slow, and then failed: every request carried every earlier tool call
/// and listing, until one request was more than the free tier takes in a minute - and Groq's "request too
/// large" was shown as "no credit left". Earlier turns are now kept as question and answer, and every request
/// is kept inside the budget.</summary>
[Collection("mind")]
public class HistoryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vigil-history-" + Guid.NewGuid().ToString("N")[..8]);

    public HistoryTests() => Mind.UseRoot(_root);

    public void Dispose()
    {
        Mind.UseRoot("");
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A model that follows a script and keeps a copy of every request it was sent.</summary>
    sealed class Scripted(string name, Func<int, AssistantMessage> reply) : IProvider
    {
        public readonly List<List<JsonObject>> Requests = new();
        public Func<int, Exception?> Fail { get; init; } = _ => null;
        public string Name => name;
        public string Model { get; set; } = "scripted";
        public long TotalTokens => 0;
        public Task<AssistantMessage> ChatAsync(IReadOnlyList<JsonObject> messages, JsonArray? tools, Action<string>? onText, CancellationToken ct)
        {
            var call = Requests.Count;
            Requests.Add(messages.Select(m => (JsonObject)m.DeepClone()).ToList());
            if (Fail(call) is { } error) throw error;
            var message = reply(call);
            if (message.Content.Length > 0) onText?.Invoke(message.Content);
            return Task.FromResult(message);
        }
        public Task<string> VisionAsync(string prompt, byte[] jpeg, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public bool SupportsTools(string model) => true;
    }

    sealed class Host : IAgentHost
    {
        public readonly List<string> Errors = new();
        public void StreamText(string chunk) { }
        public void EndStream() { }
        public void ToolStarted(string tool, string summary, Risk risk) { }
        public void ToolFinished(string tool, string output, bool ok) { }
        public void Note(string text) { }
        public void Warn(string text) { }
        public void Error(string text) => Errors.Add(text);
        public void PlanChanged(IReadOnlyList<PlanStep> steps) { }
        public string? AskUser(string question, IReadOnlyList<string> choices, CancellationToken ct) => null;
        public Answer Confirm(GuardAction action) => Answer.Yes;
        public IDisposable Driving() => new Nothing();
        public IDisposable OutOfShot() => new Nothing();
        sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>A tool that answers with a long listing, like list_dir on a full Downloads folder.</summary>
    static Registry Tools()
    {
        var registry = new Registry();
        registry.Add(new[]
        {
            new ToolSpec
            {
                Name = "list", Description = "test", Parameters = new JsonObject(), Risk = Risk.Safe,
                Handler = (_, _) => string.Join("\n", Enumerable.Range(1, 400).Select(i => "file_number_" + i + ".txt  12,345 bytes")),
            },
        });
        return registry;
    }

    static AssistantMessage Call(string tool, string id)
    {
        var message = new AssistantMessage();
        message.ToolCalls.Add(ToolCall.FromRaw(id, tool, "{\"path\":\"Downloads\"}"));
        return message;
    }

    static AssistantMessage Say(string text) => new() { Content = text };

    static int Tokens(IEnumerable<JsonObject> messages) =>
        messages.Sum(m => (Msg.Text(m).Length + (m["tool_calls"]?.ToJsonString().Length ?? 0)) / 3 + 8);

    [Fact]
    public async Task Earlier_turns_are_kept_as_what_was_asked_and_answered()
    {
        // every odd call lists, every even call answers
        var model = new Scripted("scripted", n => n % 2 == 0 ? Call("list", "c" + n) : Say("There are 400 files; the biggest is file_number_7.txt."));
        var agent = new Agent(new Config { ApprovalMode = "yolo" }, model, Tools(), new Host());

        await agent.RunAsync("What's in my Downloads?");
        await agent.RunAsync("And which is the biggest?");

        // the first request of the second turn: the first turn is two lines, not a listing
        var second = model.Requests[2];
        Assert.DoesNotContain(second, m => Msg.Role(m) == "tool");
        Assert.DoesNotContain(second, m => Msg.HasToolCalls(m));
        var earlier = second.First(m => Msg.Role(m) == "user");
        Assert.StartsWith("What's in my Downloads?", Msg.Text(earlier));
        Assert.Contains("[earlier: you used list", Msg.Text(earlier));
        Assert.DoesNotContain("Reply rule", Msg.Text(earlier));            // the notes that rode along are gone
        Assert.Contains(second, m => Msg.Role(m) == "assistant" && Msg.Text(m).Contains("biggest is file_number_7"));
    }

    [Fact]
    public async Task Compacting_twice_keeps_the_note_on_what_was_done()
    {
        var model = new Scripted("scripted", n => n % 2 == 0 ? Call("list", "c" + n) : Say("Done."));
        var agent = new Agent(new Config { ApprovalMode = "yolo" }, model, Tools(), new Host());
        await agent.RunAsync("List Downloads");
        await agent.RunAsync("List it again");
        await agent.RunAsync("Once more");
        var first = model.Requests[^2].First(m => Msg.Role(m) == "user");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Msg.Text(first), @"\[earlier:"));
    }

    [Fact]
    public async Task On_the_free_tier_no_request_goes_over_the_budget()
    {
        var model = new Scripted("groq", n => n % 2 == 0 ? Call("list", "c" + n) : Say(new string('x', 1500)));
        var agent = new Agent(new Config { ApprovalMode = "yolo" }, model, Tools(), new Host());
        for (var turn = 0; turn < 12; turn++)
            await agent.RunAsync("Turn " + turn + ": list my Downloads and tell me about it.");
        // the system prompt and the tool list ride on every request as well
        Assert.All(model.Requests, request => Assert.True(Tokens(request) < 5000, "request of " + Tokens(request) + " tokens"));
        // and the latest exchange is never the thing cut away
        Assert.Contains("Turn 11", Msg.Text(model.Requests[^1].Last(m => Msg.Role(m) == "user")));
    }

    [Fact]
    public async Task Too_large_is_answered_by_sending_less_not_by_giving_up()
    {
        var model = new Scripted("groq", _ => Say("Here you go."))
        {
            Fail = n => n == 3 ? new TooLargeException("too large") : null,
        };
        var host = new Host();
        var agent = new Agent(new Config { ApprovalMode = "yolo" }, model, Tools(), host);
        await agent.RunAsync("one");
        await agent.RunAsync("two");
        await agent.RunAsync("three");
        var answer = await agent.RunAsync("four");   // its first try is refused as too large

        Assert.Equal("Here you go.", answer);
        Assert.Empty(host.Errors);
        Assert.True(model.Requests[4].Count < model.Requests[3].Count);   // the retry carried less
    }

    [Theory]
    [InlineData(413, "Request too large for model `openai/gpt-oss-120b` on tokens per minute (TPM): Limit 8000, Requested 9605, please reduce your message size and try again. Need more tokens? Upgrade to Dev Tier today at https://console.groq.com/settings/billing")]
    [InlineData(0, "413 Request too large for model on tokens per minute (TPM)")]
    public void Groqs_too_large_is_not_an_empty_account(int status, string error) =>
        Assert.True(OpenAiCompatibleProvider.TooLarge(status, error));

    [Fact]
    public void A_real_rate_limit_is_not_too_large() =>
        Assert.False(OpenAiCompatibleProvider.TooLarge(429, "Rate limit reached for model `openai/gpt-oss-120b` on tokens per minute (TPM): Limit 8000, Used 7900, Requested 900. Please try again in 6.7s."));

    [Theory]
    [InlineData("openai/gpt-oss-120b", "low")]
    [InlineData("openai/gpt-oss-20b", "low")]
    [InlineData("qwen/qwen3.8-27b", "none")]
    [InlineData("llama-3.3-70b-versatile", null)]
    public void Running_a_pc_needs_little_thinking(string model, string? effort) =>
        Assert.Equal(effort, OpenAiCompatibleProvider.ReasoningFor(model));

    [Theory]
    [InlineData("Open Paint\n\n[Reply rule: answer what was asked. No greeting. Always reply in English.]\n[background - mention only if it helps with this request: AWARENESS: Chrome 5m]", "Open Paint")]
    [InlineData("[Spoken in Turkish, probably transcribed with mistakes - work out what they meant.]\n[background - mention only if it helps with this request: x]\n\nMadonna'dan prayer şarkısını aç", "Madonna'dan prayer şarkısını aç")]
    public void The_notes_on_a_message_come_off_when_it_is_history(string sent, string said) =>
        Assert.Equal(said, Agent.Bare(sent));
}
