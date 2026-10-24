using System.Text.Json.Nodes;
using Vigil.Core.Providers;

namespace Vigil.Voice;

/// <summary>Voicebox (voicebox.sh, MIT licence): a free voice studio that runs Kokoro, Qwen3-TTS, Chatterbox and
/// others on this computer, and answers on 127.0.0.1 in the shape of OpenAI's speech API. When it is running,
/// Vigil speaks through it - nothing leaves the machine, there is no key, and on a graphics card it is quick.
/// Vigil only talks to it over that local port; Voicebox itself is installed and run by the person, like any app.</summary>
public static class Voicebox
{
    public const string Site = "https://voicebox.sh/";
    public const string DefaultUrl = "http://127.0.0.1:17493";

    public sealed record Profile(string Id, string Name, string Language);

    static readonly object Gate = new();
    static (string Url, bool Up, DateTime At) _seen;

    /// <summary>Is Voicebox answering? Asked at most every few seconds, so a sentence never waits on it.</summary>
    public static async Task<bool> RunningAsync(string baseUrl, CancellationToken ct = default)
    {
        lock (Gate)
            if (_seen.Url == baseUrl && DateTime.UtcNow - _seen.At < (_seen.Up ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(8)))
                return _seen.Up;
        var up = false;
        try
        {
            using var request = Http.Json(HttpMethod.Get, Base(baseUrl) + "/health", null);
            using var response = await Http.SendAsync(request, false, TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            up = response.IsSuccessStatusCode;
        }
        catch (ProviderException) { }
        lock (Gate) _seen = (baseUrl, up, DateTime.UtcNow);
        return up;
    }

    /// <summary>What the last look found, without looking again - for menus.</summary>
    public static bool SeenRunning
    {
        get { lock (Gate) return _seen.Up; }
    }

    /// <summary>Forget what was last seen - after it was started, or stopped answering.</summary>
    public static void Recheck()
    {
        lock (Gate) _seen = default;
    }

    /// <summary>The voices made in Voicebox: presets and cloned ones alike.</summary>
    public static async Task<IReadOnlyList<Profile>> ProfilesAsync(string baseUrl, CancellationToken ct = default)
    {
        using var request = Http.Json(HttpMethod.Get, Base(baseUrl) + "/profiles", null);
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ProviderException("Voicebox: " + await Http.ErrorText(response).ConfigureAwait(false));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var list = body as JsonArray ?? body?["profiles"] as JsonArray ?? new JsonArray();
        return list.OfType<JsonObject>()
                   .Select(p => new Profile(p["id"]?.ToString() ?? "", p["name"]?.GetValue<string>() ?? "", p["language"]?.GetValue<string>() ?? "en"))
                   .Where(p => p.Name.Length > 0)
                   .ToList();
    }

    /// <summary>One sentence, as a WAV. `voice` is a profile name or id; empty uses Voicebox's own default voice.</summary>
    public static async Task<byte[]> SpeakAsync(string baseUrl, string voice, string text, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = "tts-1",            // "whatever engine that voice is set up with"
            ["input"] = text,
            ["response_format"] = "wav",
        };
        if (!string.IsNullOrWhiteSpace(voice)) payload["voice"] = voice;
        using var request = Http.Json(HttpMethod.Post, Base(baseUrl) + "/v1/audio/speech", payload);
        // the first sentence may wait while Voicebox loads its model onto the graphics card
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException("Voicebox: " + await Http.ErrorText(response).ConfigureAwait(false));
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Where Voicebox was installed, if it was - so it can be started from Vigil's menu.</summary>
    public static string? Installed()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var candidate in new[]
                 {
                     Path.Combine(local, "Voicebox", "Voicebox.exe"),
                     Path.Combine(local, "Programs", "Voicebox", "Voicebox.exe"),
                     Path.Combine(programs, "Voicebox", "Voicebox.exe"),
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    static string Base(string baseUrl) => (string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl).TrimEnd('/');
}
