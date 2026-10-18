using System.Text.RegularExpressions;
using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>A checklist the model keeps and the person can watch. Long jobs are where
/// agents drift; a visible plan makes them write the steps down, tick them off,
/// and shows both sides what is left. It lives in the session only.</summary>
public static class PlanTools
{
    const string Key = "plan";
    const int MaxSteps = 20;
    const int MaxStepLength = 160;
    static readonly string[] Statuses = { "todo", "doing", "done", "blocked" };

    static List<PlanStep> Plan(ToolContext ctx) =>
        ctx.State.TryGetValue(Key, out var plan) ? (List<PlanStep>)plan : new List<PlanStep>();

    public static string Render(IReadOnlyList<PlanStep> steps)
    {
        if (steps.Count == 0) return "No plan yet.";
        var icons = new Dictionary<string, string> { ["todo"] = "[ ]", ["doing"] = "[>]", ["done"] = "[x]", ["blocked"] = "[!]" };
        var lines = steps.Select((s, i) => icons.GetValueOrDefault(s.Status, "[ ]") + " " + (i + 1) + ". " + s.Text
                                           + (s.Note.Length > 0 ? "  (" + s.Note + ")" : "")).ToList();
        lines.Add("");
        lines.Add(steps.Count(s => s.Status == "done") + "/" + steps.Count + " steps complete");
        return string.Join("\n", lines);
    }

    static string Create(ToolContext ctx, ToolArgs a)
    {
        var raw = a.List("steps");
        if (raw.Count == 0) throw new ToolError("steps must be a non-empty list of strings.");
        if (raw.Count > MaxSteps) throw new ToolError("A plan may have at most " + MaxSteps + " steps. Group the small ones together.");

        var plan = raw.Select(s => Regex.Replace(s, @"\s+", " ").Trim())
                      .Where(s => s.Length > 0)
                      .Select(s => new PlanStep(Text.Clip(s, MaxStepLength), "todo"))
                      .ToList();
        if (plan.Count == 0) throw new ToolError("All steps were empty.");
        plan[0] = plan[0] with { Status = "doing" };
        ctx.State[Key] = plan;
        ctx.Host.PlanChanged(plan);
        return "Plan created with " + plan.Count + " steps. Step 1 is in progress.\n" + Render(plan);
    }

    static string Update(ToolContext ctx, ToolArgs a)
    {
        var plan = Plan(ctx);
        if (plan.Count == 0) throw new ToolError("There is no plan yet. Use create_plan first.");
        var status = a.Str("status", "done").Trim().ToLowerInvariant();
        if (!Statuses.Contains(status)) throw new ToolError("status must be one of: " + string.Join(", ", Statuses));
        var step = a.Int("step");
        if (step < 1 || step > plan.Count) throw new ToolError("There is no step " + step + ". The plan has " + plan.Count + " steps.");

        var note = Regex.Replace(a.Str("note"), @"\s+", " ").Trim();
        plan[step - 1] = plan[step - 1] with { Status = status, Note = note.Length > 0 ? Text.Clip(note, 120) : plan[step - 1].Note };
        if (status == "done")
        {
            var next = plan.FindIndex(s => s.Status == "todo");
            if (next >= 0) plan[next] = plan[next] with { Status = "doing" };
        }
        ctx.Host.PlanChanged(plan);
        var remaining = plan.Count(s => s.Status is "todo" or "doing");
        return remaining == 0
            ? "Step " + step + " marked " + status + ". Every step is finished.\n" + Render(plan)
            : "Step " + step + " marked " + status + ". " + remaining + " step(s) left.\n" + Render(plan);
    }

    static string Show(ToolContext ctx, ToolArgs _)
    {
        var plan = Plan(ctx);
        if (plan.Count > 0) ctx.Host.PlanChanged(plan);
        return Render(plan);
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "create_plan", Group = "planning", Risk = Risk.Safe, QuietResult = true,
            Description = "Write down a step-by-step plan before starting a job that needs three or more steps. Keep steps " +
                          "short and concrete - one action each. The user sees the plan. Do not use it for single-step requests.",
            Parameters = Object(P("steps", List("string", "The steps in order, one short sentence each"), true)),
            Handler = Create, Preview = a => "plan: " + a.List("steps").Count + " steps",
        },
        new()
        {
            Name = "update_plan", Group = "planning", Risk = Risk.Safe, QuietResult = true,
            Description = "Mark a step done, in progress or blocked. Call it as soon as a step finishes. When a step is " +
                          "marked done the next one starts automatically.",
            Parameters = Object(P("step", Int("Step number, starting at 1"), true), P("status", Str("", Statuses)),
                                P("note", Str("Short result or reason, optional"))),
            Handler = Update, Preview = a => "step " + a.Str("step", "?") + " -> " + a.Str("status", "done"),
        },
        new()
        {
            Name = "show_plan", Group = "planning", Risk = Risk.Safe, QuietResult = true,
            Description = "Show the current plan and what is left of it.",
            Parameters = Object(), Handler = Show, Preview = _ => "show plan",
        },
    };
}
