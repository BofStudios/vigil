using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>Vigil speaking through Voicebox: a stand-in for Voicebox on this computer answers the same three
/// calls the real one does (/health, /profiles, OpenAI-style /v1/audio/speech), and Vigil's voice goes through it -
/// but only for sentences in the language the chosen voice speaks.</summary>
public class VoiceboxTests : IDisposable
{
    readonly HttpListener _server = new();
    readonly List<JsonObject> _spoken = new();
    readonly string _url;
    readonly byte[] _wav = ElevenLabs.Wav(new byte[24000 / 5 * 2], 24000);

    public VoiceboxTests()
    {
        var port = 17000 + Random.Shared.Next(2000);
        _url = "http://localhost:" + port;
        _server.Prefixes.Add(_url + "/");
        _server.Start();
        _ = Task.Run(Serve);
        Voicebox.Recheck();
    }

    async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try { context = await _server.GetContextAsync(); }
            catch (Exception) { return; }
            var path = context.Request.Url!.AbsolutePath;
            byte[] body;
            var type = "application/json";
            if (path == "/health") body = "{\"status\":\"healthy\",\"model_loaded\":true,\"gpu_available\":true}"u8.ToArray();
            else if (path == "/profiles") body = "[{\"id\":\"p1\",\"name\":\"Morgan\",\"language\":\"en\"}]"u8.ToArray();
            else if (path == "/v1/audio/speech" && context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var request = (JsonObject)JsonNode.Parse(await reader.ReadToEndAsync())!;
                lock (_spoken) _spoken.Add(request);
                body = _wav;
                type = "audio/wav";
            }
            else
            {
                context.Response.StatusCode = 404;
                body = "{\"error\":{\"message\":\"not here\"}}"u8.ToArray();
            }
            context.Response.ContentType = type;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Close();
        Voicebox.Recheck();
    }

    [Fact]
    public async Task When_Voicebox_is_running_Vigil_speaks_through_it()
    {
        var config = new Config { TtsEngine = "auto", VoiceboxUrl = _url, VoiceboxVoice = "Morgan" };
        using var speaker = new Speaker(config);

        var wav = await speaker.SynthesiseAsync("Paint is open.", "en", CancellationToken.None);

        Assert.Equal(_wav, wav);
        var sent = Assert.Single(_spoken);
        Assert.Equal("Paint is open.", sent["input"]!.GetValue<string>());
        Assert.Equal("Morgan", sent["voice"]!.GetValue<string>());
        Assert.Equal("wav", sent["response_format"]!.GetValue<string>());
        Assert.True(Voicebox.SeenRunning);
        Assert.Contains(await Voicebox.ProfilesAsync(_url), p => p.Name == "Morgan" && p.Language == "en");
    }

    [Fact]
    public async Task A_sentence_the_voice_cannot_speak_goes_to_the_next_voice()
    {
        var config = new Config { TtsEngine = "auto", VoiceboxUrl = _url, VoiceboxVoice = "Morgan", GroqVoiceRefused = true };
        using var speaker = new Speaker(config);

        // an English voice reading Turkish mangles it: Windows' own voice takes this one
        try { await speaker.SynthesiseAsync("Paint açıldı.", "tr", CancellationToken.None); }
        catch (InvalidOperationException) { }   // no Windows voice at all on the machine: fine, the point is below

        Assert.Empty(_spoken);
    }

    [Fact]
    public void Voicebox_is_only_ever_reached_on_this_computer()
    {
        var path = Path.Combine(Path.GetTempPath(), "vigil-vb-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, "{\"voicebox_url\":\"http://203.0.113.9:17493\"}");
        try
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<Config>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower })!;
            typeof(Config).GetMethod("Repair", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(config, null);
            Assert.Equal("http://127.0.0.1:17493", config.VoiceboxUrl);
        }
        finally { File.Delete(path); }
    }
}
