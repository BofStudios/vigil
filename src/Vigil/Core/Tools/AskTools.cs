using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>Asking the person. Vigil says the question out loud when the conversation
/// is spoken, and takes the answer by voice or by keyboard.</summary>
public static class AskTools
{
    static string AskUser(ToolContext ctx, ToolArgs a)
    {
        var question = a.Req("question").Trim();
        if (question.Length == 0) throw new ToolError("There is no question to ask.");
        var choices = a.List("choices").Where(c => c.Trim().Length > 0).Take(6).ToList();
        var answer = ctx.Host.AskUser(question, choices, ctx.Cancel);
        ctx.Cancel.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(answer)
            ? "The user did not answer. Do not guess: stop and say what you needed to know."
            : "The user answered: " + answer.Trim();
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "ask_user", Group = "conversation", Risk = Risk.Safe, QuietResult = true,
            Description = "Ask the user one short question and wait for the answer - when unsure, or when the choice is theirs.",
            Parameters = Object(P("question", Str(), true), P("choices", List("string", "Optional short answers"))),
            Handler = AskUser, Preview = a => "ask: " + Text.Clip(a.Str("question"), 70),
        },
    };
}
