using System.Text.Json.Nodes;
using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>The tools as the model sees them: fewer, with one `action` choosing what each does.
///
/// Every request carries every schema, and the free tier counts tokens per minute - 47
/// separate tools cost more than half the budget before a word was said. Grouping them
/// keeps the same abilities in a third of the space. Underneath, each action is still
/// the original tool with its own checks: the guard sees exactly what it saw before.</summary>
public static class Compact
{
    static Dictionary<string, ToolSpec> Index(params ToolSpec[][] groups) =>
        groups.SelectMany(g => g).ToDictionary(s => s.Name);

    static readonly Dictionary<string, ToolSpec> Inner = Index(
        FileTools.All, ShellTools.All, SystemTools.All, ScreenTools.All, BrowserTools.All,
        PlanTools.All, MemoryTools.All, AskTools.All);

    /// <summary>A tool that hands each action to the original tool, renaming arguments on the way.</summary>
    static ToolSpec Facade(string name, string group, string description, JsonObject parameters,
                           Func<ToolArgs, (string Tool, JsonObject Args)> route, bool drives = false)
    {
        ToolSpec Pick(ToolArgs a)
        {
            var (tool, _) = route(a);
            return Inner.TryGetValue(tool, out var spec) ? spec : throw new ToolError("Unknown action for " + name + ".");
        }

        return new ToolSpec
        {
            Name = name,
            Group = group,
            Description = description,
            Parameters = parameters,
            Drives = drives,
            Handler = (ctx, a) =>
            {
                var (tool, args) = route(a);
                if (!Inner.TryGetValue(tool, out var spec)) throw new ToolError("Unknown action for " + name + ".");
                return spec.Handler(ctx, new ToolArgs(args));
            },
            RiskFn = a => { var (_, args) = route(a); return Pick(a).RiskFor(new ToolArgs(args)); },
            Preview = a => { var (_, args) = route(a); return Pick(a).Summarize(new ToolArgs(args)); },
        };
    }

    /// <summary>Copies the named arguments across, renaming where the original tool used another name.</summary>
    static JsonObject Take(ToolArgs a, params string[] names)
    {
        var copy = new JsonObject();
        foreach (var name in names)
        {
            var parts = name.Split('>');
            var from = parts[0];
            var to = parts.Length > 1 ? parts[1] : from;
            if (a.Values[from] is { } value && copy[to] is null) copy[to] = value.DeepClone();
        }
        return copy;
    }

    static string Act(ToolArgs a, string fallback = "") => a.Str("action", fallback).Trim().ToLowerInvariant();

    static ToolSpec Same(string name) => Inner[name];

    public static IReadOnlyList<ToolSpec> Build(Config config)
    {
        var tools = new List<ToolSpec>
        {
            // ---------------------------------------------------------- files
            Same("read_file"),
            Same("write_file"),
            Same("edit_file"),
            Same("list_dir"),
            Same("find_files"),
            Same("search_text"),
            Facade("manage_path", "file",
                "Make a folder, copy, move/rename, or permanently delete a file or folder.",
                Object(P("action", Str("", "make_dir", "copy", "move", "delete"), true), P("path", Str("The file or folder")),
                       P("destination", Str("For copy and move")), P("recursive", Bool("Delete a non-empty folder"))),
                a => Act(a) switch
                {
                    "make_dir" => ("make_dir", Take(a, "path")),
                    "copy" => ("copy_path", Take(a, "path>source", "source", "destination")),
                    "move" => ("move_path", Take(a, "path>source", "source", "destination")),
                    "delete" => ("delete_path", Take(a, "path", "recursive")),
                    _ => ("?", new JsonObject()),
                }),

            // ---------------------------------------------------------- shell
            Same("run_command"),
            Same("change_dir"),
            Same("ask_user"),
        };

        if (config.EnablePlanner)
            tools.Add(Facade("plan", "planning",
                "Keep a visible checklist for jobs of three or more steps: create it first, then update each step as it finishes.",
                Object(P("action", Str("", "create", "update", "show"), true), P("steps", List("string", "For create")),
                       P("step", Int("For update, 1-based")), P("status", Str("", "todo", "doing", "done", "blocked")), P("note", Str())),
                a => Act(a) switch
                {
                    "create" => ("create_plan", Take(a, "steps")),
                    "update" => ("update_plan", Take(a, "step", "status", "note")),
                    _ => ("show_plan", new JsonObject()),
                }));

        if (config.EnableMemory)
            tools.AddRange(NoteTools.All);

        if (config.EnableMemory)
            tools.Add(Facade("memory", "memory",
                "Remember a lasting fact about the user (preferences, names), recall notes, or forget them. Never store secrets.",
                Object(P("action", Str("", "remember", "recall", "forget"), true), P("text", Str("The fact, search words, or what to forget")),
                       P("scope", Str("", "global", "project", "all"))),
                a => Act(a) switch
                {
                    "remember" => ("remember", Take(a, "text>fact", "scope")),
                    "forget" => ("forget", Take(a, "text>match", "scope")),
                    _ => ("recall", Take(a, "text>query", "scope")),
                }));

        if (config.EnableSystem)
        {
            tools.Add(Facade("system", "system",
                "Read the state of the computer: info (OS, CPU, RAM, battery), time, processes, disks, network or installed apps.",
                Object(P("topic", Str("", "info", "time", "processes", "disks", "network", "apps"), true),
                       P("filter", Str("Name filter for processes or apps")), P("sort_by", Str("", "memory", "cpu"))),
                a => a.Str("topic", "info").ToLowerInvariant() switch
                {
                    "time" => ("current_time", new JsonObject()),
                    "processes" => ("list_processes", Take(a, "filter>filter_name", "sort_by")),
                    "disks" => ("disk_usage", new JsonObject()),
                    "network" => ("network_info", new JsonObject()),
                    "apps" => ("list_installed_apps", Take(a, "filter>filter_name")),
                    _ => ("system_info", new JsonObject()),
                }));
            tools.Add(Same("open_app"));
            tools.Add(Same("kill_process"));
        }

        if (config.EnableGui)
        {
            tools.Add(Facade("look", "screen",
                "See the screen: a vision model answers a question about it (and gives positions for clicking).",
                Object(P("question", Str()), P("monitor", Int("1 = primary")), P("region", List("integer", "[left, top, width, height]"))),
                a => ("screen_capture", Take(a, "question", "monitor", "region"))));
            tools.Add(Same("click_on"));
            tools.Add(Facade("mouse", "screen",
                "Click, move or scroll the mouse at screen coordinates. Prefer click_on when you can describe the target.",
                Object(P("action", Str("", "click", "move", "scroll"), true), P("x", Int()), P("y", Int()),
                       P("button", Str("", "left", "right", "middle")), P("clicks", Int()), P("amount", Int("Scroll notches, + up / - down"))),
                a => Act(a) switch
                {
                    "move" => ("mouse_move", Take(a, "x", "y")),
                    "scroll" => ("mouse_scroll", Take(a, "amount", "x", "y")),
                    _ => ("mouse_click", Take(a, "x", "y", "button", "clicks")),
                }, drives: true));
            tools.Add(Facade("keyboard", "screen",
                "Type text into the focused window, or press keys/shortcuts (enter, ctrl+c, alt+tab, win+r, volumeup, playpause).",
                Object(P("action", Str("", "type", "keys"), true), P("text", Str("For type")), P("keys", Str("For keys"))),
                a => Act(a) == "keys" || (a.Has("keys") && !a.Has("text"))
                    ? ("press_keys", Take(a, "keys", "text>keys"))
                    : ("keyboard_type", Take(a, "text")),
                drives: true));
            tools.Add(Facade("windows", "screen",
                "List open windows, or bring one to the front by title or app name.",
                Object(P("action", Str("", "list", "focus"), true), P("title", Str("For focus"))),
                a => Act(a) == "focus" ? ("focus_window", Take(a, "title")) : ("list_windows", new JsonObject())));
            tools.Add(Same("clipboard"));
        }

        if (config.EnableBrowser)
            tools.Add(Facade("browser", "browser",
                "Vigil's own browser window (separate profile): open a URL, read the page, click, type into a field, look at it, go back, close.",
                Object(P("action", Str("", "open", "read", "click", "type", "look", "back", "close"), true), P("url", Str()),
                       P("selector", Str("CSS selector or visible text/label")), P("text", Str()), P("submit", Bool()),
                       P("mode", Str("For read", "text", "links", "html")), P("question", Str("For look"))),
                a => Act(a) switch
                {
                    "open" => ("browser_open", Take(a, "url")),
                    "click" => ("browser_click", Take(a, "selector")),
                    "type" => ("browser_type", Take(a, "selector", "text", "submit")),
                    "look" => ("browser_screenshot", Take(a, "question")),
                    "back" => ("browser_back", new JsonObject()),
                    "close" => ("browser_close", new JsonObject()),
                    _ => ("browser_read", Take(a, "mode", "selector")),
                }));

        return tools;
    }
}
