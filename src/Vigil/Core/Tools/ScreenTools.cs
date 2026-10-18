using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using Vigil.Core.Security;
using Vigil.Desktop;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>The screen, the mouse and the keyboard.
///
/// Every one of these that takes control is confirmed every single time, in
/// every mode, with no "always" - that is the rule Vigil was built around - and
/// while Vigil holds the pointer or keyboard, the edge of the screen glows so
/// nobody has to wonder who is moving it.</summary>
public static class ScreenTools
{
    static readonly Regex Point = new(@"\(?\s*(\d{1,5})\s*[, ]\s*(\d{1,5})\s*\)?");

    /// <summary>Esc, pressed by a person while Vigil is driving, ends the action at once.</summary>
    static void Failsafe(ToolContext ctx)
    {
        ctx.Cancel.ThrowIfCancellationRequested();
        if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)
            throw new ToolError("Stopped: the user pressed Esc and took control back.");
    }

    static void Ask(ToolContext ctx, string tool, string summary, string reason, string detail = "")
    {
        var (allowed, why) = ctx.Guard.CheckAction(tool, summary, Risk.Moderate, reason, detail);
        if (!allowed) throw new PermissionDenied(why);
    }

    static (byte[] Jpeg, double Scale, Rectangle Area) Shoot(ToolContext ctx, int monitor, List<int>? region)
    {
        var area = ScreenGrab.Bounds(monitor);
        if (region is { Count: 4 })
        {
            if (region[2] <= 0 || region[3] <= 0) throw new ToolError("region must be [left, top, width, height].");
            area = new Rectangle(region[0], region[1], region[2], region[3]);
        }
        using (ctx.Host.OutOfShot())
        {
            using var image = ScreenGrab.Grab(area);
            var (jpeg, scale) = ScreenGrab.Encode(image);
            return (jpeg, scale, area);
        }
    }

    // ------------------------------------------------------------- look
    static string ScreenCapture(ToolContext ctx, ToolArgs a)
    {
        var question = a.Str("question").Trim();
        var prompt = question.Length > 0 ? question : "What is on this screen? Describe open windows, key text and clickable elements.";
        Ask(ctx, "screen_capture", "a screenshot will be taken and sent to the AI model: " + Text.Clip(prompt, 120),
            "screen content leaves the machine", "Everything visible (open windows, messages) goes to the model.");

        var (jpeg, scale, area) = Shoot(ctx, a.Int("monitor", 1), a.Has("region") ? a.Ints("region") : null);
        var saved = "";
        if (a.Bool("save"))
        {
            Directory.CreateDirectory(Paths.Screenshots);
            saved = Path.Combine(Paths.Screenshots, "screen_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg");
            File.WriteAllBytes(saved, jpeg);
        }

        var detailed = prompt + "\n\nThe image is " + (int)(area.Width / scale) + "x" + (int)(area.Height / scale) +
                       " pixels. If something needs to be clicked, give its approximate pixel position in the image (x, y).";
        string description;
        try { description = ctx.Wait(ct => ctx.Provider.VisionAsync(detailed, jpeg, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new ToolError("Image analysis failed: " + ex.Message); }

        var result = "[screen analysis]\n" + description;
        if (Math.Abs(scale - 1) > 0.01)
            result += "\n\n(The image was scaled by 1/" + scale.ToString("0.##") + ". Multiply image positions by " +
                      scale.ToString("0.##") + " and add the screen offset (" + area.Left + ", " + area.Top + ") to get screen coordinates for mouse_click.)";
        if (saved.Length > 0) result += "\n\nSaved to: " + saved;
        return result;
    }

    static string ClickOn(ToolContext ctx, ToolArgs a)
    {
        var target = a.Req("target").Trim();
        if (target.Length == 0) throw new ToolError("Describe what to click.");
        var button = a.Str("button", "left");
        var twice = a.Bool("double");
        Ask(ctx, "click_on", "find and click: " + target, "screen is sent to the model, then clicked",
            "A screenshot goes to the AI model to locate the element, then the mouse clicks it.");

        var (jpeg, scale, area) = Shoot(ctx, 1, null);
        var width = (int)(area.Width / scale);
        var height = (int)(area.Height / scale);
        var prompt = "Find this element on the screen: " + target + "\nThe image is " + width + " by " + height + " pixels.\n" +
                     "Reply with ONLY the centre point of the element, as two integers: x, y\n" +
                     "If you cannot find it, reply with exactly: NOT FOUND";
        string answer;
        try { answer = ctx.Wait(ct => ctx.Provider.VisionAsync(prompt, jpeg, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new ToolError("Could not look at the screen: " + ex.Message); }

        if (answer.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new ToolError("Could not find " + target + " on screen. Try screen_capture first to see what is there.");
        var match = Point.Match(answer);
        if (!match.Success) throw new ToolError("The model did not return a point. It said: " + Text.Clip(answer, 160));

        var x = (int)(int.Parse(match.Groups[1].Value) * scale) + area.Left;
        var y = (int)(int.Parse(match.Groups[2].Value) * scale) + area.Top;
        if (!area.Contains(x, y)) throw new ToolError("The located point (" + x + ", " + y + ") is off screen.");

        using (ctx.Host.Driving())
            Click(x, y, button, twice ? 2 : 1);
        return "Clicked " + target + " at (" + x + ", " + y + ").";
    }

    static string ScreenSize(ToolContext ctx, ToolArgs _)
    {
        var main = ScreenGrab.Bounds(1);
        Native.GetCursorPos(out var position);
        return "Main screen: " + main.Width + "x" + main.Height + " | Mouse at: (" + position.X + ", " + position.Y + ") | "
               + ScreenGrab.MonitorCount + " monitor(s)";
    }

    // ------------------------------------------------------------ mouse
    static void Click(int x, int y, string button, int clicks)
    {
        Native.SetCursorPos(x, y);
        Thread.Sleep(40);
        var (down, up) = button switch
        {
            "right" => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            "middle" => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP),
        };
        for (var i = 0; i < Math.Clamp(clicks, 1, 3); i++)
        {
            Native.Send(Native.Mouse(down), Native.Mouse(up));
            Thread.Sleep(60);
        }
    }

    static Rectangle Desktop()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        return screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
    }

    static string MouseClick(ToolContext ctx, ToolArgs a)
    {
        int x = a.Int("x", -1), y = a.Int("y", -1);
        var button = a.Str("button", "left");
        var clicks = Math.Clamp(a.Int("clicks", 1), 1, 3);
        if (!Desktop().Contains(x, y)) throw new ToolError("Coordinates are off screen: (" + x + ", " + y + ").");
        if (button is not ("left" or "right" or "middle")) throw new ToolError("button must be left, right or middle.");

        var summary = button + " click at (" + x + ", " + y + ")" + (clicks > 1 ? " x" + clicks : "");
        Ask(ctx, "mouse_click", summary, "mouse control");
        using (ctx.Host.Driving())
            Click(x, y, button, clicks);
        return summary + " done.";
    }

    static string MouseMove(ToolContext ctx, ToolArgs a)
    {
        int x = a.Int("x", -1), y = a.Int("y", -1);
        if (!Desktop().Contains(x, y)) throw new ToolError("Coordinates are off screen: (" + x + ", " + y + ").");
        Ask(ctx, "mouse_move", "moving the mouse to (" + x + ", " + y + ")", "mouse movement");

        var duration = Math.Clamp(a.Num("duration", 0.3), 0, 3);
        using (ctx.Host.Driving())
        {
            Native.GetCursorPos(out var from);
            var steps = Math.Max(1, (int)(duration * 60));
            var watch = Stopwatch.StartNew();
            for (var i = 1; i <= steps; i++)
            {
                Failsafe(ctx);
                var t = i / (double)steps;
                var eased = 1 - Math.Pow(1 - t, 3);
                Native.SetCursorPos((int)(from.X + (x - from.X) * eased), (int)(from.Y + (y - from.Y) * eased));
                var due = (int)(duration * 1000 * t - watch.ElapsedMilliseconds);
                if (due > 0) Thread.Sleep(due);
            }
        }
        return "Mouse moved to (" + x + ", " + y + ").";
    }

    static string MouseScroll(ToolContext ctx, ToolArgs a)
    {
        var amount = a.Int("amount");
        Ask(ctx, "mouse_scroll", "scrolling by " + amount, "page scroll");
        using (ctx.Host.Driving())
        {
            int x = a.Int("x", -1), y = a.Int("y", -1);
            if (x >= 0 && y >= 0) Native.SetCursorPos(x, y);
            Native.Send(Native.Mouse(Native.MOUSEEVENTF_WHEEL, amount * 120));
        }
        return "Scrolled by " + amount + ".";
    }

    // --------------------------------------------------------- keyboard
    static string KeyboardType(ToolContext ctx, ToolArgs a)
    {
        var text = a.Req("text");
        if (text.Length == 0) throw new ToolError("Nothing to type.");
        Ask(ctx, "keyboard_type", "typing: " + Text.Clip(text, 120), "keyboard input",
            "The text goes to whichever window currently has focus.");

        var interval = (int)Math.Clamp(a.Num("interval", 0.01) * 1000, 0, 200);
        using (ctx.Host.Driving())
        {
            foreach (var c in text.Replace("\r\n", "\n"))
            {
                Failsafe(ctx);
                if (c == '\n') Native.Send(Native.Key(0x0D, false), Native.Key(0x0D, true));
                else if (c == '\t') Native.Send(Native.Key(0x09, false), Native.Key(0x09, true));
                else Native.Send(Native.Unicode(c, false), Native.Unicode(c, true));
                if (interval > 0) Thread.Sleep(interval);
            }
        }
        return text.Length + " character(s) typed.";
    }

    static readonly Dictionary<string, (ushort Vk, bool Extended)> Keys = BuildKeys();

    static Dictionary<string, (ushort, bool)> BuildKeys()
    {
        var keys = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = (0x11, false), ["control"] = (0x11, false), ["shift"] = (0x10, false), ["alt"] = (0x12, false),
            ["win"] = (0x5B, true), ["windows"] = (0x5B, true), ["cmd"] = (0x5B, true), ["super"] = (0x5B, true),
            ["enter"] = (0x0D, false), ["return"] = (0x0D, false), ["tab"] = (0x09, false), ["esc"] = (0x1B, false),
            ["escape"] = (0x1B, false), ["space"] = (0x20, false), ["backspace"] = (0x08, false),
            ["delete"] = (0x2E, true), ["del"] = (0x2E, true), ["insert"] = (0x2D, true), ["home"] = (0x24, true),
            ["end"] = (0x23, true), ["pageup"] = (0x21, true), ["pagedown"] = (0x22, true), ["pgup"] = (0x21, true),
            ["pgdn"] = (0x22, true), ["up"] = (0x26, true), ["down"] = (0x28, true), ["left"] = (0x25, true),
            ["right"] = (0x27, true), ["capslock"] = (0x14, false), ["printscreen"] = (0x2C, true),
            ["volumeup"] = (0xAF, true), ["volumedown"] = (0xAE, true), ["volumemute"] = (0xAD, true),
            ["playpause"] = (0xB3, true), ["nexttrack"] = (0xB0, true), ["prevtrack"] = (0xB1, true),
            ["menu"] = (0x5D, true), ["apps"] = (0x5D, true),
        };
        for (var i = 1; i <= 24; i++) keys["f" + i] = ((ushort)(0x6F + i), false);
        for (var c = 'a'; c <= 'z'; c++) keys[c.ToString()] = ((ushort)char.ToUpperInvariant(c), false);
        for (var c = '0'; c <= '9'; c++) keys[c.ToString()] = (c, false);
        return keys;
    }

    static string PressKeys(ToolContext ctx, ToolArgs a)
    {
        var combo = a.Req("keys").Replace(" ", "").ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (combo.Count == 0) throw new ToolError("Key combination is empty.");
        var unknown = combo.Where(k => !Keys.ContainsKey(k)).ToList();
        if (unknown.Count > 0) throw new ToolError("Unknown key(s): " + string.Join(", ", unknown) + ".");

        Ask(ctx, "press_keys", "key: " + string.Join("+", combo), "keyboard shortcut");
        using (ctx.Host.Driving())
        {
            var codes = combo.Select(k => Keys[k]).ToList();
            var inputs = codes.Select(k => Native.Key(k.Vk, false, k.Extended))
                              .Concat(Enumerable.Reverse(codes).Select(k => Native.Key(k.Vk, true, k.Extended)))
                              .ToArray();
            Native.Send(inputs);
            Thread.Sleep(80);
        }
        return string.Join("+", combo) + " pressed.";
    }

    // ---------------------------------------------------------- windows
    static string ListWindows(ToolContext ctx, ToolArgs _)
    {
        var foreground = Native.GetForegroundWindow();
        var rows = new List<string>();
        foreach (var hwnd in Native.AppWindows().Take(40))
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            string process;
            try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
            catch (Exception) { process = "?"; }
            var state = hwnd == foreground ? "active" : Native.IsIconic(hwnd) ? "minimized" : "open";
            rows.Add("  " + Text.Clip(Native.TitleOf(hwnd), 70) + "  [" + process + ", " + state + "]");
        }
        return rows.Count == 0 ? "No open windows found." : rows.Count + " window(s):\n" + string.Join("\n", rows);
    }

    static string FocusWindow(ToolContext ctx, ToolArgs a)
    {
        var title = a.Req("title");
        var match = Native.AppWindows().FirstOrDefault(h => Native.TitleOf(h).Contains(title, StringComparison.OrdinalIgnoreCase));
        if (match == IntPtr.Zero)
        {
            // a process name works too: "chrome", "spotify"
            match = Native.AppWindows().FirstOrDefault(h =>
            {
                Native.GetWindowThreadProcessId(h, out var pid);
                try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.Contains(title, StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { return false; }
            });
        }
        if (match == IntPtr.Zero) throw new ToolError("No window with that title: " + title);

        var name = Native.TitleOf(match);
        var (allowed, reason) = ctx.Guard.CheckAction("focus_window", "bringing to front: " + Text.Clip(name, 80), Risk.Moderate, "window focus");
        if (!allowed) throw new PermissionDenied(reason);
        Native.Focus(match);
        Thread.Sleep(300);
        return name + " brought to front.";
    }

    // -------------------------------------------------------- clipboard
    static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new ToolError("Clipboard is busy: " + error.Message);
        return result;
    }

    static string Clipboard(ToolContext ctx, ToolArgs a)
    {
        if (a.Str("action", "read") == "write")
        {
            var text = a.Str("text");
            Ask(ctx, "clipboard", "writing to the clipboard: " + Text.Clip(text, 80), "clipboard change");
            OnSta(() => { System.Windows.Clipboard.SetText(text); return true; });
            return "Clipboard updated (" + text.Length + " characters).";
        }
        Ask(ctx, "clipboard", "reading the clipboard", "clipboard read (may contain secrets)");
        var content = OnSta(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "");
        if (content.Length > 4000) content = content[..4000] + "\n... (truncated)";
        return content.Length == 0 ? "The clipboard holds no text." : "Clipboard contents:\n" + content;
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "screen_capture", Group = "screen", Risk = Risk.Moderate,
            Description = "Capture the screen and have a vision model analyse it. Use it to see what is on screen, " +
                          "locate an element or check the state of an application.",
            Parameters = Object(P("question", Str("What to ask about the screen (empty = general description)")),
                                P("monitor", Int("Monitor number (1 = primary)")),
                                P("region", List("integer", "Optional region [left, top, width, height]")),
                                P("save", Bool("Also save the image to disk"))),
            Handler = ScreenCapture, Preview = a => "screen analysis: " + Text.Clip(a.Str("question"), 60),
        },
        new()
        {
            Name = "click_on", Group = "screen", Risk = Risk.Moderate, Drives = true,
            Description = "Find something on screen by description ('the Save button') and click it.",
            Parameters = Object(P("target", Str(), true), P("button", Str("", "left", "right", "middle")), P("double", Bool())),
            Handler = ClickOn, Preview = a => "find and click: " + a.Str("target"),
        },
        new()
        {
            Name = "screen_size", Group = "screen", Risk = Risk.Safe,
            Description = "Return the screen resolution and the current mouse position.",
            Parameters = Object(), Handler = ScreenSize, Preview = _ => "screen size",
        },
        new()
        {
            Name = "mouse_click", Group = "screen", Risk = Risk.Moderate, Drives = true,
            Description = "Click at the given screen coordinates (physical pixels).",
            Parameters = Object(P("x", Int(), true), P("y", Int(), true), P("button", Str("", "left", "right", "middle")), P("clicks", Int())),
            Handler = MouseClick, Preview = a => "click (" + a.Str("x") + ", " + a.Str("y") + ")",
        },
        new()
        {
            Name = "mouse_move", Group = "screen", Risk = Risk.Moderate, Drives = true,
            Description = "Move the mouse to the given coordinates.",
            Parameters = Object(P("x", Int(), true), P("y", Int(), true), P("duration", Num("Seconds the move takes"))),
            Handler = MouseMove, Preview = a => "mouse -> (" + a.Str("x") + ", " + a.Str("y") + ")",
        },
        new()
        {
            Name = "mouse_scroll", Group = "screen", Risk = Risk.Moderate, Drives = true,
            Description = "Scroll up (positive) or down (negative), in wheel notches.",
            Parameters = Object(P("amount", Int("Positive scrolls up, negative scrolls down"), true), P("x", Int()), P("y", Int())),
            Handler = MouseScroll, Preview = a => "scroll " + a.Str("amount"),
        },
        new()
        {
            Name = "keyboard_type", Group = "screen", Risk = Risk.Moderate, Drives = true,
            Description = "Type text into the focused window. Any language works. Never type passwords or secrets.",
            Parameters = Object(P("text", Str(), true), P("interval", Num("Seconds between characters"))),
            Handler = KeyboardType, Preview = a => "type: " + Text.Clip(a.Str("text"), 60),
        },
        new()
        {
            Name = "press_keys", Group = "screen", Risk = Risk.Moderate, Drives = true,
            Description = "Send a key or shortcut. Examples: enter, tab, ctrl+c, alt+f4, win+r, volumeup, playpause",
            Parameters = Object(P("keys", Str(), true)),
            Handler = PressKeys, Preview = a => "key: " + a.Str("keys"),
        },
        new()
        {
            Name = "list_windows", Group = "screen", Risk = Risk.Safe,
            Description = "List open windows, their app and state.",
            Parameters = Object(), Handler = ListWindows, Preview = _ => "list windows",
        },
        new()
        {
            Name = "focus_window", Group = "screen", Risk = Risk.Moderate,
            Description = "Bring a window to the front, by part of its title or its app name.",
            Parameters = Object(P("title", Str("Window title or app name (partial match)"), true)),
            Handler = FocusWindow, Preview = a => "focus window: " + a.Str("title"),
        },
        new()
        {
            Name = "clipboard", Group = "screen", Risk = Risk.Moderate,
            Description = "Read or write the clipboard.",
            Parameters = Object(P("action", Str("", "read", "write")), P("text", Str())),
            Handler = Clipboard, Preview = a => "clipboard " + a.Str("action", "read"),
        },
    };
}
