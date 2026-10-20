using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>Notes in Vigil Mind - plain .md files in Documents\Vigil Mind\Notes that the
/// person can open, edit and keep. Vigil's own folder, so writing there is not asked about;
/// deleting a note is.</summary>
public static class NoteTools
{
    static string Notes(ToolContext ctx, ToolArgs a)
    {
        var action = a.Str("action", "list").Trim().ToLowerInvariant();
        var title = a.Str("title").Trim();
        var text = a.Str("text");
        try
        {
            switch (action)
            {
                case "write":
                case "append":
                    if (title.Length == 0) throw new ToolError("A note needs a title.");
                    if (text.Trim().Length == 0) throw new ToolError("There is nothing to write.");
                    var file = Mind.WriteNote(title, text, append: action == "append");
                    Mind.Journal("note", (action == "append" ? "added to " : "wrote ") + "\"" + Mind.Slug(title) + "\"");
                    return "Saved to " + file;

                case "read":
                    if (title.Length == 0) throw new ToolError("Which note?");
                    return Mind.ReadNote(title) is { } body
                        ? Text.Truncate(body, ctx.Config.MaxToolOutput)
                        : "There is no note called \"" + title + "\". Notes: " + string.Join(", ", Mind.ListNotes().Take(30).Select(n => n.Title));

                case "search":
                    var query = text.Length > 0 ? text : title;
                    var hits = Mind.SearchNotes(query);
                    return hits.Count == 0 ? "No note mentions \"" + query + "\"."
                        : string.Join("\n", hits.Select(h => h.Title + ": " + h.Line));

                case "delete":
                    var (allowed, reason) = ctx.Guard.CheckAction("notes", "delete the note \"" + title + "\"", Risk.Moderate, "deleting a note");
                    if (!allowed) throw new PermissionDenied(reason);
                    return Mind.DeleteNote(title) ? "Deleted \"" + title + "\"." : "There is no note called \"" + title + "\".";

                default:
                    var notes = Mind.ListNotes();
                    return notes.Count == 0 ? "Vigil Mind has no notes yet."
                        : notes.Count + " note(s), newest first:\n" + string.Join("\n", notes.Take(60).Select(n => "- " + n.Title + "  (" + n.Changed.ToString("yyyy-MM-dd HH:mm") + ")"));
            }
        }
        catch (IOException ex) { throw new ToolError("Could not use Vigil Mind: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new ToolError("Could not use Vigil Mind: " + ex.Message); }
    }

    static string Journal(ToolContext ctx, ToolArgs a)
    {
        var day = DateTime.Now.Date;
        var which = a.Str("day", "today").Trim().ToLowerInvariant();
        if (which == "yesterday") day = day.AddDays(-1);
        else if (which is "last" or "previous") day = Mind.LastDayBefore(day) ?? day.AddDays(-1);
        else if (DateTime.TryParse(which, out var parsed)) day = parsed.Date;

        var lines = Mind.JournalLines(day, 80);
        var activity = Activity.SummaryFor(day);
        if (lines.Count == 0 && activity.Length == 0) return "Nothing in the journal for " + day.ToString("yyyy-MM-dd") + ".";
        return day.ToString("dddd yyyy-MM-dd") + "\n" + string.Join("\n", lines) + (activity.Length > 0 ? "\n\nApps in front: " + activity : "");
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "notes", Group = "memory", Risk = Risk.Safe,
            Description = "The user's notes in Vigil Mind (Documents\\Vigil Mind\\Notes): write, append, read, search, list or delete. " +
                          "Take notes whenever something is worth keeping - ideas, plans, decisions, things to remember.",
            Parameters = Object(P("action", Str("", "write", "append", "read", "search", "list", "delete"), true),
                                P("title", Str()), P("text", Str("For write/append, or the search words"))),
            Handler = Notes, Preview = a => "notes " + a.Str("action", "list") + (a.Str("title").Length > 0 ? ": " + a.Str("title") : ""),
        },
        new()
        {
            Name = "journal", Group = "memory", Risk = Risk.Safe,
            Description = "Read Vigil's journal: what was asked and done on a day, and which apps were in front. " +
                          "Use it to pick up where the user left off.",
            Parameters = Object(P("day", Str("today, yesterday, last, or yyyy-MM-dd"))),
            Handler = Journal, Preview = a => "journal: " + a.Str("day", "today"),
        },
    };
}
