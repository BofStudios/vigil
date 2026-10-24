using System.Text.Json.Nodes;
using Vigil.Core.Providers;

namespace Vigil.Core;

/// <summary>Work mode: Vigil as a second brain at the next desk. It watches how the work is going - which app is
/// in front, for how long, how often you jump between them, how long since a break; app names and minutes only,
/// never the screen, never window titles, never what is typed - and after the first stretch (ten minutes by
/// default), and every stretch after, interrupts with the one thing worth saying: you have drifted from what you
/// set out to do, you are scattered across six apps, it is time for a break, or the smart next step.
/// Do not disturb still wins: while it is on, work mode watches but says nothing.</summary>
public sealed class WorkMode
{
    readonly Config _config;
    DateTime _started = DateTime.Now;
    DateTime _lastSaid = DateTime.Now;
    readonly List<string> _said = new();

    public WorkMode(Config config) => _config = config;

    public bool On => _config.WorkMode;
    public DateTime Started => _started;

    /// <summary>`at`: when it started - now, except in tests.</summary>
    public void Begin(string goal, DateTime? at = null)
    {
        _config.WorkMode = true;
        _config.WorkGoal = (goal ?? "").Trim();
        _started = _lastSaid = at ?? DateTime.Now;
        _said.Clear();
    }

    /// <summary>Picks up after Vigil restarted with work mode still on.</summary>
    public void Resume()
    {
        _started = _lastSaid = DateTime.Now;
    }

    /// <summary>Ends it, and says how the session went.</summary>
    public string End()
    {
        _config.WorkMode = false;
        var stretch = Activity.Between(_started, DateTime.Now);
        var minutes = (DateTime.Now - _started).TotalMinutes;
        return minutes < 2 || stretch.Active < 60
            ? "Work mode off."
            : "Work mode off after " + Activity.Duration((DateTime.Now - _started).TotalSeconds) + ": " +
              string.Join(", ", stretch.Apps.Where(a => a.Seconds >= 60).Take(4).Select(a => a.App + " " + Activity.Duration(a.Seconds))) +
              ", " + stretch.Switches + " switches.";
    }

    /// <summary>Time for a word? Only after a full stretch of actual work - an empty desk is not nagged.</summary>
    public bool Due()
    {
        if (!On || _config.Quiet) return false;
        var every = TimeSpan.FromMinutes(_config.WorkMinutes);
        if (DateTime.Now - _lastSaid < every) return false;
        if (Activity.Away || Activity.Between(_lastSaid, DateTime.Now).Active < every.TotalSeconds * 0.35)
        {
            _lastSaid = DateTime.Now - every / 2;   // look again in half a stretch
            return false;
        }
        return true;
    }

    /// <summary>The one thing worth saying about the last stretch, written by the model - one short call,
    /// no tools - with a plain rule-of-thumb line if the model is not reachable.</summary>
    public async Task<string> AdviseAsync(IProvider provider, CancellationToken ct)
    {
        var now = DateTime.Now;
        var stretch = Activity.Between(_lastSaid, now);
        var session = Activity.Between(_started, now);
        var goal = _config.WorkGoal.Trim();
        var name = _config.UserName.Trim();
        var journal = Mind.JournalLines(now.Date, 6);

        var prompt =
            "Work mode is on: they asked you to watch how their work goes and interrupt with advice.\n" +
            "What they are working on: " + (goal.Length > 0 ? "\"" + goal + "\"" : "they did not say") + ".\n" +
            "The last " + Activity.Duration((now - _lastSaid).TotalSeconds) + ": " + Activity.Describe(stretch) + ".\n" +
            "The whole session so far (" + Activity.Duration((now - _started).TotalSeconds) + "): " + Activity.Describe(session) + ".\n" +
            (journal.Count > 0 ? "What they asked you lately:\n" + string.Join("\n", journal) + "\n" : "") +
            (_said.Count > 0 ? "You already told them (do not repeat): " + string.Join(" | ", _said.TakeLast(3)) + "\n" : "") +
            "\nWrite ONE interruption: at most two short sentences, like a sharp friend at the next desk. Say what you " +
            "notice, with a number, and one concrete thing to do right now:\n" +
            "- jumping between many apps: name the ones pulling them away (chat, social, video) and tell them to close or " +
            "mute those and give the goal the next 25 minutes;\n" +
            "- most of the time in apps that are not the work: bring them back to the goal, kindly and plainly;\n" +
            "- over 75 minutes without a break: a five-minute break, now;\n" +
            "- going well: say so in a few words and name a good next step.\n" +
            "You know app names and times only, not what is on screen - never pretend otherwise. " +
            "English, no greeting, no emoji, no lecture.";

        var messages = new List<JsonObject>
        {
            Msg.System("You are Vigil, the user's second brain and closest friend at their computer" +
                       (name.Length > 0 ? "; their name is " + name : "") + ". Direct, warm, brief."),
            Msg.User(prompt),
        };
        string? text = null;
        for (var attempt = 1; attempt <= 2 && text is null; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var reply = await provider.ChatAsync(messages, null, null, timeout.Token).ConfigureAwait(false);
                var written = ThinkFilter.Strip(reply.Content).Trim().Trim('"');
                if (written.Length is > 0 and < 420) text = written;
                else break;
            }
            catch (RateLimitException limit) when (attempt == 1 && limit.RetryAfter <= TimeSpan.FromSeconds(40))
            {
                try { await Task.Delay(limit.RetryAfter + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex) when (ex is ProviderException or OperationCanceledException or HttpRequestException) { break; }
        }
        text ??= Fallback(stretch, goal);
        _lastSaid = now;
        _said.Add(text);
        return text;
    }

    /// <summary>The rule of thumb, for when the model cannot be reached.</summary>
    public static string Fallback(Activity.Stretch stretch, string goal)
    {
        if (stretch.SinceBreak >= 75 * 60)
            return Activity.Duration(stretch.SinceBreak) + " without a break. Stand up for five minutes - you'll come back sharper.";
        if (stretch.Switches >= 12)
            return stretch.Switches + " app switches in this stretch. Pick the one thing that matters" +
                   (goal.Length > 0 ? " for " + goal : "") + " and give it the next 25 minutes.";
        var top = stretch.Apps.FirstOrDefault();
        return top.App is { Length: > 0 }
            ? "Mostly " + top.App + " this stretch (" + Activity.Duration(top.Seconds) + "). Keep going - what's the next concrete step?"
            : "Quiet stretch. What's the next concrete step?";
    }
}
