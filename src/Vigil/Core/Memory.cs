using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vigil.Core;

/// <summary>Notes Vigil carries across sessions, in plain text files the person
/// can open and edit. Two scopes: global (everywhere) and project (one folder).</summary>
public static class Memory
{
    const int MaxEntries = 200;
    const int MaxPromptChars = 2500;

    static string GlobalFile => Path.Combine(Paths.Memory, "global.md");
    static string ProjectDir => Path.Combine(Paths.Memory, "projects");

    public static string ProjectFile(string cwd)
    {
        var resolved = Path.GetFullPath(cwd).TrimEnd('\\', '/');
        var digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(resolved)))[..8].ToLowerInvariant();
        var name = Regex.Replace(Path.GetFileName(resolved), "[^a-zA-Z0-9_-]", "_");
        if (name.Length == 0) name = "root";
        return Path.Combine(ProjectDir, name + "_" + digest + ".md");
    }

    static List<string> Read(string path)
    {
        if (!File.Exists(path))
            return new();
        try
        {
            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();
        }
        catch (IOException) { return new(); }
    }

    static void Write(string path, List<string> entries, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var kept = entries.Skip(Math.Max(0, entries.Count - MaxEntries));
        File.WriteAllText(path, "# " + title + "\n\n" + string.Join("\n", kept) + "\n");
    }

    static (string Path, string Title) Target(string scope, string cwd) =>
        scope == "global" ? (GlobalFile, "Vigil global memory") : (ProjectFile(cwd), "Vigil project memory");

    static IEnumerable<(string Name, string Path)> Sources(string scope, string cwd) => scope switch
    {
        "global" => new[] { ("global", GlobalFile) },
        "project" => new[] { ("project", ProjectFile(cwd)) },
        _ => new[] { ("global", GlobalFile), ("project", ProjectFile(cwd)) },
    };

    public static string Add(string fact, string scope, string cwd)
    {
        fact = Regex.Replace(fact ?? "", @"\s+", " ").Trim();
        if (fact.Length == 0)
            throw new ArgumentException("Cannot store an empty note.");

        var (path, title) = Target(scope, cwd);
        var entries = Read(path);
        var lowered = fact.ToLowerInvariant();
        foreach (var entry in entries)
            if (entry.ToLowerInvariant().Contains(lowered))
                return "Already in memory: " + Clip(entry, 120);

        entries.Add("- [" + DateTime.Now.ToString("yyyy-MM-dd") + "] " + fact);
        Write(path, entries, title);
        return "Stored in memory (" + scope + "): " + Clip(fact, 150);
    }

    public static List<(string Scope, string Entry)> Search(string query, string scope, string cwd)
    {
        var results = new List<(string, string)>();
        foreach (var (name, path) in Sources(scope, cwd))
            foreach (var entry in Read(path))
                if (string.IsNullOrEmpty(query) || entry.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add((name, entry));
        return results;
    }

    public static string Remove(string match, string scope, string cwd)
    {
        match = (match ?? "").Trim();
        if (match.Length == 0)
            throw new ArgumentException("Search text cannot be empty.");

        var removed = 0;
        foreach (var (name, path) in Sources(scope, cwd))
        {
            var entries = Read(path);
            var kept = entries.Where(e => !e.Contains(match, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count != entries.Count)
            {
                removed += entries.Count - kept.Count;
                Write(path, kept, name == "global" ? "Vigil global memory" : "Vigil project memory");
            }
        }
        return removed == 0 ? "No matching note found: " + match : removed + " note(s) deleted.";
    }

    /// <summary>What goes into the system prompt. Empty when there is nothing.</summary>
    public static string AsPrompt(string cwd)
    {
        var parts = new List<string>();
        var global = Read(GlobalFile);
        var project = Read(ProjectFile(cwd));
        if (global.Count > 0)
            parts.Add("General notes:\n" + string.Join("\n", global.TakeLast(40)));
        if (project.Count > 0)
            parts.Add("Notes for this folder:\n" + string.Join("\n", project.TakeLast(40)));
        if (parts.Count == 0)
            return "";
        var text = string.Join("\n\n", parts);
        if (text.Length > MaxPromptChars)
        {
            text = text[^MaxPromptChars..];
            text = text[(text.IndexOf('\n') + 1)..];
        }
        return text;
    }

    static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}
