using System.Text.Json.Nodes;
using Vigil.Core.Providers;
using Vigil.Core.Tools;
using Vigil.Voice;

namespace Vigil.Tests;

public class VoiceAndTextTests
{
    [Theory]
    [InlineData("Hey Vigil, open Notepad.", "Open Notepad.")]
    [InlineData("hey vigil Spotify'ı aç", "Spotify'ı aç")]
    [InlineData("Okay Vigil what time is it?", "What time is it?")]
    [InlineData("Vigil, stop", "Stop")]
    [InlineData("Open the vigil folder", "Open the vigil folder")]
    public void The_wake_phrase_is_taken_off_the_request(string heard, string expected) =>
        Assert.Equal(expected, SpeechText.StripWake(heard));

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("Altyazı M.K.")]
    [InlineData("  ")]
    [InlineData("...")]
    public void Whisper_inventions_on_silence_are_ignored(string heard) =>
        Assert.True(SpeechText.IsNoise(heard));

    [Fact]
    public void A_real_request_is_not_noise() => Assert.False(SpeechText.IsNoise("Thank you, now open Spotify"));

    [Fact]
    public void Markdown_is_not_read_out_loud()
    {
        var spoken = SpeechText.ForSpeech("## Done\n- **Spotify** is open, see [the docs](https://x.y).\n```\ncode\n```\nEnjoy `it`.");
        Assert.DoesNotContain("#", spoken);
        Assert.DoesNotContain("*", spoken);
        Assert.DoesNotContain("http", spoken);
        Assert.DoesNotContain("code", spoken);
        Assert.Contains("Spotify is open, see the docs.", spoken);
    }

    [Fact]
    public void Sentences_come_out_as_soon_as_they_are_complete()
    {
        var splitter = new SentenceSplitter();
        Assert.Empty(splitter.Push("Spotify is ope"));
        Assert.Equal(new[] { "Spotify is open." }, splitter.Push("n. Shall I also"));
        Assert.Empty(splitter.Push(" start a playlist"));
        Assert.Equal(new[] { "Shall I also start a playlist?" }, splitter.Push("?").Concat(splitter.Flush()));
    }

    [Fact]
    public void Code_blocks_are_never_spoken_even_across_chunks()
    {
        var splitter = new SentenceSplitter();
        var said = new List<string>();
        foreach (var chunk in new[] { "Here it is.\n``", "`python\nprint(1)\n`", "``\nRun it when ready." })
            said.AddRange(splitter.Push(chunk));
        said.AddRange(splitter.Flush());
        Assert.Equal(new[] { "Here it is.", "Run it when ready." }, said);
    }

    [Theory]
    [InlineData("Bunu şimdi açıyorum, istersen sesi de kısarım.", "tr")]
    [InlineData("Hello there.", "en")]
    [InlineData("Привет", "ru")]
    public void The_reply_language_is_guessed_from_its_letters(string text, string code) =>
        Assert.Equal(code, SpeechText.GuessLanguage(text, "en"));

    [Theory]
    [InlineData("turkish", "tr")]
    [InlineData("tr-TR", "tr")]
    [InlineData("English", "en")]
    [InlineData("", "en")]
    public void Language_names_and_codes_both_work(string language, string code) =>
        Assert.Equal(code, Speaker.Code(language));

    [Fact]
    public void Thinking_is_filtered_out_of_the_stream_even_when_the_tag_is_split()
    {
        var filter = new ThinkFilter();
        var visible = filter.Push("Hi <thi") + filter.Push("nk>secret plans</th") + filter.Push("ink>there") + filter.Flush();
        Assert.Equal("Hi there", visible);
    }

    // ------------------------------------------------------- providers
    [Fact]
    public void Claude_gets_the_system_prompt_as_a_parameter_and_tool_results_together()
    {
        var call = new AssistantMessage { Content = "Checking." };
        call.ToolCalls.Add(ToolCall.FromRaw("a", "list_dir", "{\"path\":\".\"}"));
        call.ToolCalls.Add(ToolCall.FromRaw("b", "current_time", "{}"));
        var messages = new List<JsonObject>
        {
            Msg.System("be brief"),
            Msg.User("look around"),
            call.ToMessage(),
            Msg.ToolResult("a", "list_dir", "two files"),
            Msg.ToolResult("b", "current_time", "noon"),
        };
        var (system, converted) = AnthropicProvider.ToAnthropic(messages);
        Assert.Equal("be brief", system);
        Assert.Equal(3, converted.Count);
        var results = (JsonArray)converted[2]!["content"]!;
        Assert.Equal(2, results.Count);
        Assert.Equal("tool_result", results[0]!["type"]!.GetValue<string>());
        var uses = (JsonArray)converted[1]!["content"]!;
        Assert.Equal("tool_use", uses[1]!["type"]!.GetValue<string>());
        Assert.Equal(".", uses[1]!["input"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void Broken_tool_arguments_become_an_empty_object() =>
        Assert.Empty(ToolCall.FromRaw("x", "t", "{not json").Arguments);

    [Fact]
    public void Tool_arguments_are_read_forgivingly()
    {
        var args = new ToolArgs(JsonNode.Parse("{\"n\":\"5\",\"b\":\"yes\",\"list\":\"[\\\"a\\\",\\\"b\\\"]\",\"one\":\"solo\"}")!.AsObject());
        Assert.Equal(5, args.Int("n"));
        Assert.True(args.Bool("b"));
        Assert.Equal(new[] { "a", "b" }, args.List("list"));
        Assert.Equal(new[] { "solo" }, args.List("one"));
        Assert.Throws<ToolError>(() => args.Req("missing"));
    }

    [Fact]
    public void Long_output_keeps_its_head_and_tail()
    {
        var text = new string('a', 10000) + "END";
        var cut = Text.Truncate(text, 1000);
        Assert.True(cut.Length < 1200);
        Assert.EndsWith("END", cut);
        Assert.Contains("characters skipped", cut);
    }
}
