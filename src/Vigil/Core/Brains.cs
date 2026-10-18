namespace Vigil.Core;

/// <summary>One way of working, and the words that describe it.
///
/// A brain changes the approach section of the system prompt, how many steps a
/// run may take, whether planning tools are offered, and which model thinks.
/// It never changes security: blocked stays blocked and the mouse, keyboard and
/// screen are confirmed every time in both. That is why there is no security
/// field here at all.</summary>
public sealed record Brain(
    string Key,
    string Name,
    string Tagline,
    string Summary,
    string Model,
    int MaxSteps,
    bool Plans,
    string Approach,
    string Warning = "");

public static class Brains
{
    public const string Direct = "direct";
    public const string Autonomous = "autonomous";

    const string DirectApproach = """
        APPROACH - DIRECT: do exactly what was asked, by the shortest correct route, and stop when it is done.
        Ask (ask_user) rather than guess when a request is ambiguous. No plans - the user wants the result.
        """;

    const string AutonomousApproach = """
        APPROACH - AUTONOMOUS: you are handed problems, not instructions - work out the route. For three or more
        steps, plan(create) first and plan(update) each step as it finishes; the user watches the plan. Check before
        deciding, change course when something fails, verify at the end and say plainly what you could not confirm.
        """;

    public static readonly IReadOnlyDictionary<string, Brain> All = new Dictionary<string, Brain>
    {
        [Direct] = new(
            Direct, "Direct", "Does what you ask",
            "Follows your instruction step by step and stops when it is finished. " +
            "Best for everyday jobs - open this, rename that, find that file.",
            "openai/gpt-oss-20b", 14, false, DirectApproach),
        [Autonomous] = new(
            Autonomous, "Autonomous", "Works out how",
            "Give it a problem rather than an instruction and it plans a route, tries things, " +
            "and changes course when they do not work. Best for when you do not know the steps yourself.",
            "openai/gpt-oss-120b", 40, true, AutonomousApproach,
            "Takes many more actions on its own before it comes back to you. It still asks before " +
            "touching your mouse, keyboard or screen - that never changes - but it will travel " +
            "further on a wrong idea before you see it."),
    };

    public static Brain Get(string? key) =>
        All.TryGetValue((key ?? "").Trim().ToLowerInvariant(), out var brain) ? brain : All[Direct];

    public static IReadOnlyList<Brain> InOrder => new[] { All[Direct], All[Autonomous] };
}
