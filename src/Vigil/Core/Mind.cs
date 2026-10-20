using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vigil.Core;

/// <summary>Vigil Mind: a folder on this computer that Vigil writes its life into, in plain
/// files a person can open with Notepad.
///
///   Vigil Mind\
///     Journal\2026-10-20.md   what was asked, what Vigil did, how it went - with times
///     Notes\&lt;title&gt;.md        notes, written by Vigil or by hand
///     Activity\2026-10-20.txt which apps were in front today, and for how long
///     README.txt              what this folder is
///
/// Nothing in it ever leaves the machine on its own.</summary>
public static class Mind
{
    static readonly object Gate = new();

    public static string Root { get; private set; } = DefaultRoot();

    static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vigil Mind");

    public static string JournalDir => Path.Combine(Root, "Journal");
    public static string NotesDir => Path.Combine(Root, "Notes");
    public static string ActivityDir => Path.Combine(Root, "Activity");

    /// <summary>Points Mind somewhere else (settings, or a test's temp folder).</summary>
    public static void UseRoot(string? folder) => Root = string.IsNullOrWhiteSpace(folder) ? DefaultRoot() : folder;

    public static void Ensure()
    {
        foreach (var dir in new[] { Root, JournalDir, NotesDir, ActivityDir })
            Directory.CreateDirectory(dir);
        var readme = Path.Combine(Root, "README.txt");
        if (!File.Exists(readme))
            File.WriteAllText(readme,
                "Vigil Mind\r\n\r\n" +
                "This is Vigil's memory on this computer.\r\n\r\n" +
                "Journal\\   one file a day: what you asked, what Vigil did, and how it went.\r\n" +
                "Notes\\     notes Vigil wrote for you (and any you add yourself - it reads them too).\r\n" +
                "Activity\\  which apps you had in front each day, and for how long. Only app names,\r\n" +
                "           never what is on the screen or what you type. Turn it off in Vigil's settings.\r\n\r\n" +
                "Everything here stays on this computer. Delete any of it whenever you like.\r\n");
    }

    // ================================================================ journal
    static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string JournalFile(DateTime day) =>
        Path.Combine(JournalDir, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");

    /// <summary>One line in today's journal, stamped with the time.</summary>
    public static void Journal(string kind, string text)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return;
        if (text.Length > 400) text = text[..397] + "...";
        try
        {
            lock (Gate)
            {
                Ensure();
                var file = JournalFile(DateTime.Now);
                if (!File.Exists(file))
                    File.WriteAllText(file, "# " + DateTime.Now.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture) + "\r\n\r\n");
                File.AppendAllText(file, "- " + DateTime.Now.ToString("HH:mm") + "  **" + kind + "**  " + text + "\r\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The last lines of a day's journal, newest last.</summary>
    public static List<string> JournalLines(DateTime day, int last = 60)
    {
        var file = JournalFile(day);
        if (!File.Exists(file)) return new();
        try
        {
            return File.ReadAllLines(file).Where(l => l.StartsWith("- ")).TakeLast(last).ToList();
        }
        catch (IOException) { return new(); }
    }

    /// <summary>The most recent day before today that has a journal.</summary>
    public static DateTime? LastDayBefore(DateTime day)
    {
        if (!Directory.Exists(JournalDir)) return null;
        return Directory.GetFiles(JournalDir, "*.md")
            .Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                DateTimeStyles.None, out var d) ? d : (DateTime?)null)
            .Where(d => d is not null && d.Value.Date < day.Date)
            .OrderByDescending(d => d)
            .FirstOrDefault();
    }

    // ================================================================== notes
    public static string Slug(string title)
    {
        var clean = Regex.Replace(title.Trim(), @"[\\/:*?""<>|]+", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim().TrimEnd('.');
        if (clean.Length > 80) clean = clean[..80].Trim();
        return clean.Length == 0 ? "Untitled" : clean;
    }

    public static string NoteFile(string title) => Path.Combine(NotesDir, Slug(title) + ".md");

    public static string WriteNote(string title, string text, bool append)
    {
        lock (Gate)
        {
            Ensure();
            var file = NoteFile(title);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (append && File.Exists(file))
                File.AppendAllText(file, "\r\n\r\n_" + stamp + "_\r\n\r\n" + text.Trim() + "\r\n");
            else
                File.WriteAllText(file, "# " + Slug(title) + "\r\n\r\n_" + stamp + "_\r\n\r\n" + text.Trim() + "\r\n");
            return file;
        }
    }

    public static string? ReadNote(string title)
    {
        var file = NoteFile(title);
        if (File.Exists(file)) return File.ReadAllText(file);
        // a near match: "shopping" finds "Shopping list"
        var match = ListNotes().FirstOrDefault(n => n.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null ? null : File.ReadAllText(match.Path);
    }

    public sealed record NoteInfo(string Title, string Path, DateTime Changed);

    public static List<NoteInfo> ListNotes()
    {
        if (!Directory.Exists(NotesDir)) return new();
        return Directory.GetFiles(NotesDir, "*.md")
            .Select(f => new NoteInfo(Path.GetFileNameWithoutExtension(f), f, File.GetLastWriteTime(f)))
            .OrderByDescending(n => n.Changed)
            .ToList();
    }

    public static bool DeleteNote(string title)
    {
        var file = NoteFile(title);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    public static List<(string Title, string Line)> SearchNotes(string query, int limit = 30)
    {
        var hits = new List<(string, string)>();
        foreach (var note in ListNotes())
        {
            string[] lines;
            try { lines = File.ReadAllLines(note.Path); }
            catch (IOException) { continue; }
            foreach (var line in lines)
                if (line.Contains(query, StringComparison.OrdinalIgnoreCase) || note.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add((note.Title, line.Trim()));
                    if (hits.Count >= limit) return hits;
                    break;
                }
        }
        return hits;
    }

    /// <summary>What goes in the system prompt: the note titles, so the model knows what it can read.</summary>
    public static string NotesForPrompt(int max = 25)
    {
        var notes = ListNotes();
        if (notes.Count == 0) return "";
        var sb = new StringBuilder("Notes in Vigil Mind (read one with notes/read): ");
        sb.Append(string.Join(", ", notes.Take(max).Select(n => "\"" + n.Title + "\"")));
        if (notes.Count > max) sb.Append(" and " + (notes.Count - max) + " more");
        return sb.ToString();
    }
}
