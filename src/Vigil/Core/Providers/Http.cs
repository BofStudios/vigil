using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Vigil.Core.Providers;

/// <summary>One HTTP client for the whole app, and the small pieces every provider
/// needs: JSON posts, server-sent events, retries and readable errors.</summary>
public static class Http
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,  // each call carries its own deadline
    };

    static Http()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Vigil/2.0 (BOF Studios)");
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, JsonNode? body,
                                          IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        foreach (var (key, value) in headers ?? Array.Empty<KeyValuePair<string, string>>())
            request.Headers.TryAddWithoutValidation(key, value);
        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool stream,
                                                            TimeSpan deadline, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(deadline);
        try
        {
            return await Client.SendAsync(request,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("The request timed out. Check your connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("Connection error - " + ex.Message);
        }
    }

    /// <summary>Server-sent events: yields the payload of every `data:` line.</summary>
    public static async IAsyncEnumerable<string> ReadEvents(HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                yield break;
            if (data.Length > 0)
                yield return data;
        }
    }

    /// <summary>Newline-delimited JSON (Ollama).</summary>
    public static async IAsyncEnumerable<string> ReadLines(HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            if (line.Trim().Length > 0)
                yield return line;
    }

    public static async Task<string> ErrorText(HttpResponseMessage response)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch (Exception) { body = ""; }
        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.GetValue<string>()
                          ?? node?["error"]?.GetValue<string>()
                          ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
                body = message;
        }
        catch (Exception) { }
        if (body.Length > 400) body = body[..400];
        return ((int)response.StatusCode) + " " + body;
    }

    public static bool Retryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.InternalServerError || (int)status == 529;

    public static MultipartFormDataContent Form(byte[] file, string fileName, string mediaType,
                                                IEnumerable<(string Key, string Value)> fields)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(content, "file", fileName);
        foreach (var (key, value) in fields)
            if (!string.IsNullOrEmpty(value))
                form.Add(new StringContent(value), key);
        return form;
    }
}

/// <summary>Some models think out loud between &lt;think&gt; tags. That is not for the
/// person, and it is certainly not for reading aloud, so it is filtered out of
/// the stream as it arrives - including tags split across two chunks.</summary>
public sealed class ThinkFilter
{
    const string Open = "<think>";
    const string Close = "</think>";
    readonly StringBuilder _pending = new();
    bool _inside;

    public string Push(string chunk)
    {
        _pending.Append(chunk);
        var output = new StringBuilder();
        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tag = _inside ? Close : Open;
            var at = text.IndexOf(tag, StringComparison.Ordinal);
            if (at >= 0)
            {
                if (!_inside) output.Append(text, 0, at);
                _pending.Remove(0, at + tag.Length);
                _inside = !_inside;
                continue;
            }
            // keep back anything that could be the start of a tag
            var keep = 0;
            for (var n = Math.Min(tag.Length - 1, text.Length); n > 0; n--)
                if (tag.StartsWith(text[^n..], StringComparison.Ordinal)) { keep = n; break; }
            if (!_inside) output.Append(text, 0, text.Length - keep);
            _pending.Remove(0, text.Length - keep);
            break;
        }
        return output.ToString();
    }

    public string Flush()
    {
        var rest = _inside ? "" : _pending.ToString();
        _pending.Clear();
        return rest;
    }

    public static string Strip(string text)
    {
        var filter = new ThinkFilter();
        return (filter.Push(text) + filter.Flush()).Trim();
    }
}
