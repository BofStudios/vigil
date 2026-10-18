using System.Text;
using System.Text.RegularExpressions;
using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>Read, write, edit, search, move, copy and delete. Every path passes the
/// guard; secrets are never read and system folders are never written.</summary>
public static class FileTools
{
    const long MaxReadBytes = 400_000;

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", ".venv", "venv", "dist", "build", "bin", "obj",
        ".next", ".idea", ".pytest_cache", ".ruff_cache", "site-packages", ".cache", ".vs",
    };

    static readonly HashSet<string> Binary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".mp3", ".mp4", ".avi", ".wav",
        ".mkv", ".zip", ".rar", ".7z", ".gz", ".tar", ".exe", ".dll", ".so", ".dylib", ".msi",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pyc", ".class", ".bin",
    };

    static string Require(ToolContext ctx, string tool, string path, bool write = false, string detail = "", string? raw = null)
    {
        var resolved = ctx.Resolve(path);
        var (allowed, reason) = ctx.Guard.CheckPath(tool, resolved, write, detail, raw ?? path);
        if (!allowed)
            throw new PermissionDenied(reason);
        return resolved;
    }

    static string ReadText(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
                throw new ToolError("This looks like a binary file, not text.");
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
        }
        catch (IOException ex) { throw new ToolError("Could not read the file: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new ToolError("Permission denied: " + ex.Message); }
    }

    // ------------------------------------------------------------------ read
    static string ReadFile(ToolContext ctx, ToolArgs a)
    {
        var target = Require(ctx, "read_file", a.Req("path"));
        if (Directory.Exists(target)) throw new ToolError(target + " is a directory. Use list_dir.");
        if (!File.Exists(target)) throw new ToolError("File not found: " + target);
        var info = new FileInfo(target);
        if (Binary.Contains(info.Extension))
            return "[binary file] " + target + " (" + Text.Human(info.Length) + ") - not readable as text.";
        if (info.Length > MaxReadBytes)
            throw new ToolError("File is too large (" + Text.Human(info.Length) + "). Read it in chunks with offset/limit, or use search_text.");

        var lines = ReadText(target).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var start = Math.Max(1, a.Int("offset", 1)) - 1;
        var end = Math.Min(lines.Length, start + Math.Max(1, a.Int("limit", 400)));
        var numbered = new StringBuilder();
        for (var i = start; i < end; i++)
            numbered.Append(i + 1).Append('\t').Append(lines[i]).Append('\n');
        var header = target + " (" + lines.Length + " lines";
        if (end < lines.Length) header += ", showing " + (start + 1) + "-" + end;
        return header + ")\n" + Text.Truncate(numbered.ToString(), ctx.Config.MaxToolOutput);
    }

    // ----------------------------------------------------------------- write
    static string WriteFile(ToolContext ctx, ToolArgs a)
    {
        var raw = a.Req("path");
        var content = a.Str("content");
        var target = ctx.Resolve(raw);
        var exists = File.Exists(target);
        string detail;
        try { detail = exists ? DiffPreview(ReadText(target), content) : PreviewBlock(content); }
        catch (ToolError) { detail = "(existing content could not be previewed)"; }

        target = Require(ctx, "write_file", target, true, detail, raw);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolError("Write failed: " + ex.Message);
        }
        return target + " " + (exists ? "updated" : "created") + " (" + content.Split('\n').Length + " lines).";
    }

    // ------------------------------------------------------------------ edit
    static string EditFile(ToolContext ctx, ToolArgs a)
    {
        var raw = a.Req("path");
        var oldText = a.Req("old_text");
        var newText = a.Str("new_text");
        var replaceAll = a.Bool("replace_all");
        var target = ctx.Resolve(raw);
        if (!File.Exists(target)) throw new ToolError("File not found: " + target);
        if (oldText.Length == 0) throw new ToolError("old_text cannot be empty.");

        var original = ReadText(target);
        var count = Regex.Matches(original, Regex.Escape(oldText)).Count;
        if (count == 0) throw new ToolError("The text was not found in the file. Read it first to check the exact content.");
        if (count > 1 && !replaceAll)
            throw new ToolError("The text appears " + count + " times. Add more context or pass replace_all=true.");

        var at = original.IndexOf(oldText, StringComparison.Ordinal);
        var updated = replaceAll ? original.Replace(oldText, newText) : original[..at] + newText + original[(at + oldText.Length)..];
        target = Require(ctx, "edit_file", target, true, DiffPreview(original, updated), raw);
        try { File.WriteAllText(target, updated, new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ToolError("Write failed: " + ex.Message); }
        return target + " edited (" + (replaceAll ? count : 1) + " replacement(s)).";
    }

    // ------------------------------------------------------------------ list
    static string ListDir(ToolContext ctx, ToolArgs a)
    {
        var target = Require(ctx, "list_dir", a.Str("path", "."));
        if (File.Exists(target)) return ReadFile(ctx, new ToolArgs(new() { ["path"] = target }));
        if (!Directory.Exists(target)) throw new ToolError("Directory not found: " + target);
        var showHidden = a.Bool("show_hidden");
        var limit = Math.Max(1, a.Int("limit", 200));

        var entries = new List<string>();
        try
        {
            var dir = new DirectoryInfo(target);
            foreach (var item in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                if (showHidden || (!item.Name.StartsWith('.') && !item.Attributes.HasFlag(FileAttributes.Hidden)))
                    entries.Add("[dir]  " + item.Name + "/");
            foreach (var item in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                if (showHidden || (!item.Name.StartsWith('.') && !item.Attributes.HasFlag(FileAttributes.Hidden)))
                    entries.Add("[file] " + item.Name + "  " + Text.Human(item.Length));
        }
        catch (UnauthorizedAccessException ex) { throw new ToolError("Permission denied for this directory: " + ex.Message); }

        var header = target + " - " + entries.Count + " item(s)";
        if (entries.Count > limit) header += " (showing first " + limit + ")";
        return header + "\n" + string.Join("\n", entries.Take(limit));
    }

    static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            foreach (var file in files) yield return file;
            foreach (var dir in dirs.Reverse())
            {
                var name = Path.GetFileName(dir);
                if (!SkipDirs.Contains(name) && !name.StartsWith('.'))
                    stack.Push(dir);
            }
        }
    }

    static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ find
    static string FindFiles(ToolContext ctx, ToolArgs a)
    {
        var pattern = a.Req("pattern");
        var root = Require(ctx, "find_files", a.Str("path", "."));
        if (!Directory.Exists(root)) throw new ToolError("Not a directory: " + root);
        var limit = Math.Max(1, a.Int("limit", 100));
        var glob = Glob(pattern);
        var matches = Walk(root).Where(f => glob.IsMatch(Path.GetFileName(f))).Take(limit).ToList();
        return matches.Count == 0
            ? "No matches for " + pattern + " in " + root
            : matches.Count + " match(es):\n" + string.Join("\n", matches);
    }

    // ---------------------------------------------------------------- search
    static string SearchText(ToolContext ctx, ToolArgs a)
    {
        var query = a.Req("query");
        var root = Require(ctx, "search_text", a.Str("path", "."));
        var glob = Glob(a.Str("file_pattern", "*"));
        var limit = Math.Max(1, a.Int("limit", 80));
        Regex regex;
        try { regex = new Regex(query, a.Bool("ignore_case", true) ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { throw new ToolError("Invalid search pattern: " + ex.Message); }

        var files = File.Exists(root) ? new[] { root }
            : Walk(root).Where(f => glob.IsMatch(Path.GetFileName(f)) && !Binary.Contains(Path.GetExtension(f)));
        var hits = new List<string>();
        foreach (var file in files)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            if (Rules.ClassifyPath(file).Blocked) continue;   // never search inside secrets
            try
            {
                if (new FileInfo(file).Length > MaxReadBytes) continue;
                var number = 0;
                foreach (var line in File.ReadLines(file))
                {
                    number++;
                    if (regex.IsMatch(line))
                    {
                        hits.Add(file + ":" + number + ": " + Text.Clip(line.TrimEnd(), 220));
                        if (hits.Count >= limit) break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
            if (hits.Count >= limit) break;
        }
        return hits.Count == 0 ? "No results for: " + query
            : hits.Count + " result(s):\n" + Text.Truncate(string.Join("\n", hits), ctx.Config.MaxToolOutput);
    }

    // ------------------------------------------------------------ move, copy
    static string MovePath(ToolContext ctx, ToolArgs a)
    {
        var source = Require(ctx, "move_path", a.Req("source"), true);
        var destination = Require(ctx, "move_path", a.Req("destination"), true, source + " -> " + a.Str("destination"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else if (File.Exists(source)) File.Move(source, destination);
            else throw new ToolError("Source not found: " + source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ToolError("Move failed: " + ex.Message); }
        return source + " -> " + destination + " moved.";
    }

    static string CopyPath(ToolContext ctx, ToolArgs a)
    {
        var source = Require(ctx, "copy_path", a.Req("source"));
        var destination = Require(ctx, "copy_path", a.Req("destination"), true, source + " -> " + a.Str("destination"));
        try
        {
            if (Directory.Exists(source)) CopyTree(source, destination);
            else if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            else throw new ToolError("Source not found: " + source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ToolError("Copy failed: " + ex.Message); }
        return source + " -> " + destination + " copied.";
    }

    static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    // ---------------------------------------------------------------- delete
    static string DeletePath(ToolContext ctx, ToolArgs a)
    {
        var raw = a.Req("path");
        var target = ctx.Resolve(raw);
        string detail;
        if (Directory.Exists(target))
        {
            var count = Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Take(100_000).Count();
            detail = "DIRECTORY TO DELETE: " + target + " (" + count + " item(s) inside)";
            if (count > 0 && !a.Bool("recursive"))
                throw new ToolError("Directory is not empty. Pass recursive=true to delete it.");
        }
        else if (File.Exists(target))
            detail = "FILE TO DELETE: " + target + " (" + Text.Human(new FileInfo(target).Length) + ")";
        else
            throw new ToolError("Not found: " + target);

        // system folders and secrets first: a blocked path is refused without being asked about
        var verdict = ctx.Guard.JudgePath(target, true, raw);
        var (allowed, reason) = verdict.Blocked
            ? ctx.Guard.Evaluate(new GuardAction("delete_path", detail, verdict, detail))
            : ctx.Guard.CheckAction("delete_path", detail, Risk.High, "permanent deletion", detail);
        if (!allowed) throw new PermissionDenied(reason);

        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            else File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ToolError("Delete failed: " + ex.Message); }
        return target + " deleted.";
    }

    static string MakeDir(ToolContext ctx, ToolArgs a)
    {
        var target = Require(ctx, "make_dir", a.Req("path"), true);
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ToolError("Could not create the directory: " + ex.Message); }
        return target + " is ready.";
    }

    // --------------------------------------------------------------- previews
    static string PreviewBlock(string content, int limit = 24)
    {
        var lines = content.Split('\n');
        var text = string.Join("\n", lines.Take(limit).Select(l => "+ " + l.TrimEnd('\r')));
        if (lines.Length > limit) text += "\n+ ... (" + (lines.Length - limit) + " more lines)";
        return text;
    }

    /// <summary>A compact diff for the approval card: the changed middle with three lines
    /// of context either side.</summary>
    public static string DiffPreview(string before, string after, int context = 3)
    {
        var a = before.Replace("\r", "").Split('\n');
        var b = after.Replace("\r", "").Split('\n');
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        if (prefix == a.Length && prefix == b.Length) return "(content is identical)";
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        var lines = new List<string> { "--- current", "+++ new" };
        var start = Math.Max(0, prefix - context);
        lines.Add("@@ line " + (start + 1) + " @@");
        for (var i = start; i < prefix; i++) lines.Add("  " + a[i]);
        for (var i = prefix; i < a.Length - suffix; i++) lines.Add("- " + a[i]);
        for (var i = prefix; i < b.Length - suffix; i++) lines.Add("+ " + b[i]);
        for (var i = a.Length - suffix; i < Math.Min(a.Length, a.Length - suffix + context); i++) lines.Add("  " + a[i]);
        if (lines.Count > 60) lines = lines.Take(60).Append("... (diff truncated)").ToList();
        return string.Join("\n", lines);
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "read_file", Group = "file", Risk = Risk.Safe,
            Description = "Read a text file with line numbers (offset/limit for big ones).",
            Parameters = Object(P("path", Str(), true), P("offset", Int()), P("limit", Int())),
            Handler = ReadFile, Preview = a => "read: " + a.Str("path"),
        },
        new()
        {
            Name = "write_file", Group = "file", Risk = Risk.Moderate,
            Description = "Write a whole file (prefer edit_file for small changes).",
            Parameters = Object(P("path", Str(), true), P("content", Str(), true)),
            Handler = WriteFile, Preview = a => "write: " + a.Str("path"),
        },
        new()
        {
            Name = "edit_file", Group = "file", Risk = Risk.Moderate,
            Description = "Replace exact text in a file.",
            Parameters = Object(P("path", Str(), true), P("old_text", Str(), true), P("new_text", Str(), true), P("replace_all", Bool())),
            Handler = EditFile, Preview = a => "edit: " + a.Str("path"),
        },
        new()
        {
            Name = "list_dir", Group = "file", Risk = Risk.Safe,
            Description = "List the contents of a directory.",
            Parameters = Object(P("path", Str()), P("show_hidden", Bool()), P("limit", Int())),
            Handler = ListDir, Preview = a => "list: " + a.Str("path", "."),
        },
        new()
        {
            Name = "find_files", Group = "file", Risk = Risk.Safe,
            Description = "Find files by name pattern, e.g. *.pdf, report*.",
            Parameters = Object(P("pattern", Str(), true), P("path", Str()), P("limit", Int())),
            Handler = FindFiles, Preview = a => "find files: " + a.Str("pattern"),
        },
        new()
        {
            Name = "search_text", Group = "file", Risk = Risk.Safe,
            Description = "Search inside files for text or a regex.",
            Parameters = Object(P("query", Str(), true), P("path", Str()), P("file_pattern", Str()), P("limit", Int())),
            Handler = SearchText, Preview = a => "search: " + a.Str("query"),
        },
        new()
        {
            Name = "make_dir", Group = "file", Risk = Risk.Moderate,
            Description = "Create a directory, including parent directories.",
            Parameters = Object(P("path", Str(), true)),
            Handler = MakeDir, Preview = a => "mkdir: " + a.Str("path"),
        },
        new()
        {
            Name = "copy_path", Group = "file", Risk = Risk.Moderate,
            Description = "Copy a file or directory.",
            Parameters = Object(P("source", Str(), true), P("destination", Str(), true)),
            Handler = CopyPath, Preview = a => "copy: " + a.Str("source") + " -> " + a.Str("destination"),
        },
        new()
        {
            Name = "move_path", Group = "file", Risk = Risk.Moderate,
            Description = "Move or rename a file or directory.",
            Parameters = Object(P("source", Str(), true), P("destination", Str(), true)),
            Handler = MovePath, Preview = a => "move: " + a.Str("source") + " -> " + a.Str("destination"),
        },
        new()
        {
            Name = "delete_path", Group = "file", Risk = Risk.High,
            Description = "Permanently delete a file or directory. Always asks for confirmation.",
            Parameters = Object(P("path", Str(), true), P("recursive", Bool("true for non-empty directories"))),
            Handler = DeletePath, Preview = a => "DELETE: " + a.Str("path"),
        },
    };
}
