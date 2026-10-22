using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using static Vigil.Core.Tools.Schema;

namespace Vigil.Core.Tools;

/// <summary>Playing something: a song, a video, a podcast. It finds the best match on YouTube and
/// opens it - playing - in the person's own default browser. No mouse, no keyboard, nothing to approve
/// beyond opening a page.</summary>
public static class MediaTools
{
    static readonly Regex FirstVideo = new(
        "\"videoRenderer\":\\{\"videoId\":\"(?<id>[\\w-]{11})\".*?\"title\":\\{\"runs\":\\[\\{\"text\":\"(?<title>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>The first real video for a search, as (id, title); null when there is none.</summary>
    public static async Task<(string Id, string Title)?> FindAsync(string query, CancellationToken ct)
    {
        // sp=EgIQAQ%3D%3D: videos only - no channels, playlists or shorts shelves first
        var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + "&sp=EgIQAQ%253D%253D";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+1; SOCS=CAI");   // past the cookie page, nothing else
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ToolError("YouTube did not answer (" + (int)response.StatusCode + ").");
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Match match;
        try { match = FirstVideo.Match(html); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!match.Success) return null;
        return (match.Groups["id"].Value, Unescape(match.Groups["title"].Value));
    }

    static string Unescape(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string>("\"" + json + "\"") ?? json; }
        catch (System.Text.Json.JsonException) { return json; }
    }

    static string Play(ToolContext ctx, ToolArgs a)
    {
        var query = a.Req("query").Trim();
        if (query.Length == 0) throw new ToolError("What should play?");

        (string Id, string Title)? found;
        try { found = FindAsync(query, ctx.Cancel).GetAwaiter().GetResult(); }
        catch (ProviderException ex) { throw new ToolError("Could not search YouTube: " + ex.Message); }
        if (found is not { } video) throw new ToolError("Nothing on YouTube matched \"" + query + "\". Try the exact title and artist.");

        var watch = "https://www.youtube.com/watch?v=" + video.Id;
        var (allowed, reason) = ctx.Guard.CheckAction("play", "playing: " + video.Title, Risk.Moderate, "opening a video in your browser");
        if (!allowed) throw new PermissionDenied(reason);
        try
        {
            // the person's own default browser, where they are signed in and autoplay is allowed
            Process.Start(new ProcessStartInfo(watch) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new ToolError("Could not open the browser: " + ex.Message); }
        Mind.Journal("played", video.Title);
        return "Playing \"" + WebUtility.HtmlDecode(video.Title) + "\" on YouTube in the default browser (" + watch + ").";
    }

    public static readonly ToolSpec[] All =
    {
        new()
        {
            Name = "play", Group = "system", Risk = Risk.Moderate,
            Description = "Play a song, video or podcast: finds it on YouTube and opens it playing in the user's browser. " +
                          "Give the real title and artist (resolve what they meant first - \"Madonna's prayer song\" is \"Madonna Like a Prayer\").",
            Parameters = Object(P("query", Str("Artist and exact title, e.g. \"Madonna Like a Prayer\""), true)),
            Handler = Play, Preview = a => "play: " + a.Str("query"),
        },
    };
}
