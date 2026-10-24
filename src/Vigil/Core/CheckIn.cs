using System.Text.Json.Nodes;
using Vigil.Core.Providers;

namespace Vigil.Core;

/// <summary>Now and then Vigil checks in, the way a friend at the next desk would: after a
/// long stretch in one app, or when you come back after a break. One or two sentences,
/// ending in a question - never a lecture, never more often than the settings allow.</summary>
public sealed class CheckIn
{
    readonly Config _config;
    DateTime _last = DateTime.Now;          // no check-in in the first stretch after starting
    DateTime _awaySince = DateTime.MinValue;
    TimeSpan _wasAwayFor = TimeSpan.Zero;

    public CheckIn(Config config) => _config = config;

    public sealed record Moment(string Reason, string App, TimeSpan For);

    /// <summary>Called about once a minute. Returns a reason to check in, or null.</summary>
    public Moment? Due()
    {
        // work mode has its own, closer watch: one voice at a time
        if (!_config.Awareness || !_config.CheckIns || _config.Quiet || _config.WorkMode) return null;

        // coming back after a real break is its own moment
        if (Activity.Away)
        {
            if (_awaySince == DateTime.MinValue) _awaySince = DateTime.Now - Activity.IdleAfter;
            return null;
        }
        if (_awaySince != DateTime.MinValue)
        {
            _wasAwayFor = DateTime.Now - _awaySince;
            _awaySince = DateTime.MinValue;
            if (_wasAwayFor >= TimeSpan.FromMinutes(20) && DateTime.Now - _last >= TimeSpan.FromMinutes(15))
            {
                _last = DateTime.Now;
                return new Moment("back", Activity.Current, _wasAwayFor);
            }
        }

        var every = TimeSpan.FromMinutes(_config.CheckInMinutes);
        if (Activity.Current.Length > 0 && Activity.Streak >= every && DateTime.Now - _last >= every)
        {
            _last = DateTime.Now;
            return new Moment("streak", Activity.Current, Activity.Streak);
        }
        return null;
    }

    /// <summary>Writes the check-in with the model - no tools, one short call - and falls back
    /// to a plain line if the model is not reachable.</summary>
    public async Task<string> WriteAsync(IProvider provider, Moment moment, CancellationToken ct)
    {
        var name = _config.UserName.Trim();
        var situation = moment.Reason == "back"
            ? "They just came back to the computer after " + Activity.Duration(moment.For.TotalSeconds) + " away" +
              (moment.App.Length > 0 ? ", now in " + moment.App : "") + "."
            : "They have been in " + moment.App + " for " + Activity.Duration(moment.For.TotalSeconds) + " without a break.";
        var today = Activity.SummaryFor(DateTime.Now.Date);
        var journal = Mind.JournalLines(DateTime.Now.Date, 8);
        if (journal.Count == 0 && Mind.LastDayBefore(DateTime.Now.Date) is { } last) journal = Mind.JournalLines(last, 6);

        var prompt =
            situation + (today.Length > 0 ? " Today so far: " + today + "." : "") +
            (journal.Count > 0 ? "\nRecent journal:\n" + string.Join("\n", journal) : "") +
            "\n\nWrite one check-in to them: at most two short sentences, warm and a little witty. Give one concrete, useful piece " +
            "of advice for what they are doing - a tip, a smarter next step, or a break if it has been very long - and end with " +
            "one question. English. No greeting formula, no emoji, no lecture.";

        var messages = new List<JsonObject>
        {
            Msg.System("You are Vigil, the user's friend and assistant on their computer" +
                       (name.Length > 0 ? "; their name is " + name : "") + ". You only know app names and times, not what is on screen."),
            Msg.User(prompt),
        };
        // a check-in is never urgent: out of quota for the minute, it waits once and tries again
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var reply = await provider.ChatAsync(messages, null, null, timeout.Token).ConfigureAwait(false);
                var text = ThinkFilter.Strip(reply.Content).Trim().Trim('"');
                if (text.Length is > 0 and < 400) return text;
                break;
            }
            catch (RateLimitException limit) when (attempt == 1 && limit.RetryAfter <= TimeSpan.FromSeconds(40))
            {
                try { await Task.Delay(limit.RetryAfter + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex) when (ex is ProviderException or OperationCanceledException or HttpRequestException) { break; }
        }
        return Fallback(moment);
    }

    public static string Fallback(Moment moment) => moment.Reason == "back"
        ? "Welcome back. Want to pick up where you left off?"
        : moment.For >= TimeSpan.FromHours(2)
            ? "That's " + Activity.Duration(moment.For.TotalSeconds) + " straight in " + moment.App + ". Stretch your legs, or shall I help you finish it faster?"
            : "Still deep in " + moment.App + ". How's it going - anything I can take off your plate?";
}
