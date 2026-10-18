using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>Remembering across sessions.</summary>
public static class MemoryTools
{
    static string Remember(ToolContext ctx, ToolArgs a)
    {
        var scope = a.Str("scope", "global");
        if (scope is not ("project" or "global")) throw new ToolError("scope must be 'project' or 'global'.");
        try { return Memory.Add(a.Req("fact"), scope, ctx.Cwd); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { throw new ToolError("Could not write to memory: " + ex.Message); }
    }

    static string Recall(ToolContext ctx, ToolArgs a)
    {
        var query = a.Str("query");
        var results = Memory.Search(query, a.Str("scope", "all"), ctx.Cwd);
        if (results.Count == 0) return "Nothing in memory matches" + (query.Length > 0 ? ": " + query : ".");
        return results.Count + " note(s):\n" + Text.Truncate(string.Join("\n", results.Select(r => r.Scope + ": " + r.Entry)), ctx.Config.MaxToolOutput);
    }

    static string Forget(ToolContext ctx, ToolArgs a)
    {
        var match = a.Req("match");
        var scope = a.Str("scope", "all");
        var hits = Memory.Search(match, scope, ctx.Cwd);
        if (hits.Count == 0) return "No matching note found: " + match;

        var preview = string.Join("\n", hits.Take(10).Select(h => h.Scope + ": " + h.Entry));
        var (allowed, reason) = ctx.Guard.CheckAction("forget", hits.Count + " note(s) will be deleted", Risk.Moderate,
                                                      "permanent deletion from memory", preview);
        if (!allowed) throw new PermissionDenied(reason);
        try { return Memory.Remove(match, scope, ctx.Cwd); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { throw new ToolError("Delete failed: " + ex.Message); }
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "remember", Group = "memory", Risk = Risk.Safe,
            Description = "Store a fact that should still be known in future sessions: the user's preferences, names, " +
                          "recurring facts. Do not store temporary or trivial details, and never secrets.",
            Parameters = Object(P("fact", Str("The fact to remember, one sentence"), true),
                                P("scope", Str("global = everywhere (default), project = this folder only", "global", "project"))),
            Handler = Remember, Preview = a => "remember: " + Text.Clip(a.Str("fact"), 70),
        },
        new()
        {
            Name = "recall", Group = "memory", Risk = Risk.Safe,
            Description = "Search stored notes. An empty query returns every note.",
            Parameters = Object(P("query", Str("Word to search for (empty = all)")), P("scope", Str("", "all", "project", "global"))),
            Handler = Recall, Preview = a => "recall: " + (a.Str("query").Length > 0 ? a.Str("query") : "everything"),
        },
        new()
        {
            Name = "forget", Group = "memory", Risk = Risk.Moderate,
            Description = "Delete stored notes that contain the given text.",
            Parameters = Object(P("match", Str("Text contained in the notes to delete"), true), P("scope", Str("", "all", "project", "global"))),
            Handler = Forget, Preview = a => "forget: " + a.Str("match"),
        },
    };
}
