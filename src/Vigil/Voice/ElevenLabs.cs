using System.Text.Json.Nodes;
using Vigil.Core.Providers;

namespace Vigil.Voice;

/// <summary>ElevenLabs: the most natural voice Vigil can use, in every language it speaks.
/// Your own key (there is a free monthly allowance); the text of each sentence is sent to
/// ElevenLabs to be spoken, nothing else.</summary>
public static class ElevenLabs
{
    public const string KeysUrl = "https://elevenlabs.io/app/settings/api-keys";

    /// <summary>A few of ElevenLabs' own ready-made voices - every account has them.</summary>
    public static readonly (string Id, string Name, string About)[] Voices =
    {
        ("onwK4e9ZLuTAKqWW03F9", "Daniel", "British, deep and calm"),
        ("JBFqnCBsd6RMkjVDRZzb", "George", "British, warm"),
        ("nPczCjzI2devNBz1zQrb", "Brian", "American, deep"),
        ("pNInz6obpgDQGcFmaJgB", "Adam", "American, clear"),
        ("XB0fDUnXU5powFXDhCwa", "Charlotte", "soft, a woman's voice"),
    };

    public static string NameOf(string id) => Voices.FirstOrDefault(v => v.Id == id).Name ?? "custom voice";

    const int Rate = 24000;

    public static async Task<byte[]> SpeakAsync(string key, string voiceId, string model, string text, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["text"] = text,
            ["model_id"] = model,
            ["voice_settings"] = new JsonObject { ["stability"] = 0.45, ["similarity_boost"] = 0.8, ["style"] = 0.15 },
        };
        var url = "https://api.elevenlabs.io/v1/text-to-speech/" + Uri.EscapeDataString(voiceId) + "?output_format=pcm_" + Rate;
        using var request = Http.Json(HttpMethod.Post, url, payload, new Dictionary<string, string> { ["xi-api-key"] = key });
        using var response = await Http.SendAsync(request, false, TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException("ElevenLabs: " + await Error(response).ConfigureAwait(false));
        var pcm = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Wav(pcm, Rate);
    }

    /// <summary>ElevenLabs says what went wrong under "detail".</summary>
    static async Task<string> Error(HttpResponseMessage response)
    {
        var body = "";
        try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch (Exception) { }
        try
        {
            var detail = JsonNode.Parse(body)?["detail"];
            var message = detail is JsonObject ? detail["message"]?.GetValue<string>() : detail?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message)) return (int)response.StatusCode + " " + message;
        }
        catch (Exception) { }
        return (int)response.StatusCode + " " + (body.Length > 200 ? body[..200] : body);
    }

    /// <summary>Raw 16-bit mono PCM, with the 44-byte header that makes it a WAV file.</summary>
    public static byte[] Wav(byte[] pcm, int rate)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);        // PCM
        w.Write((short)1);        // mono
        w.Write(rate);
        w.Write(rate * 2);        // bytes a second
        w.Write((short)2);        // bytes a sample
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return stream.ToArray();
    }
}
