using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vigil.Core;

namespace Vigil.Desktop;

/// <summary>Vigil's own browser: the Edge (or Chrome) already on the machine,
/// started with a profile of its own and driven over the DevTools protocol.
///
/// A profile of its own means it never sees the person's logins, cookies or
/// saved passwords - a page Vigil opens cannot reach their accounts.</summary>
internal sealed class Cdp : IDisposable
{
    readonly ClientWebSocket _socket = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    readonly CancellationTokenSource _closing = new();
    int _nextId;
    Process? _process;

    public int Port { get; private set; }

    static readonly (string Key, string Path)[] Browsers =
    {
        ("chrome", @"Google\Chrome\Application\chrome.exe"),
        ("edge", @"Microsoft\Edge\Application\msedge.exe"),
        ("brave", @"BraveSoftware\Brave-Browser\Application\brave.exe"),
    };

    /// <summary>The browser the person uses: the Windows default if it is one Vigil can drive, then
    /// Chrome, Edge, Brave - whichever is installed. Always with Vigil's own profile, never theirs.</summary>
    public static string? FindBrowser()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        string? Installed(string key) => Browsers.Where(b => b.Key == key)
            .SelectMany(b => roots.Select(r => Path.Combine(r, b.Path))).FirstOrDefault(File.Exists);

        var preferred = DefaultBrowser();
        return (preferred is null ? null : Installed(preferred)) ?? Browsers.Select(b => Installed(b.Key)).FirstOrDefault(p => p is not null);
    }

    /// <summary>"chrome", "edge" or "brave" from the Windows default for web links; null for anything else.</summary>
    public static string? DefaultBrowser()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = key?.GetValue("ProgId") as string ?? "";
            return progId.StartsWith("ChromeHTML", StringComparison.OrdinalIgnoreCase) ? "chrome"
                 : progId.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) ? "edge"
                 : progId.StartsWith("BraveHTML", StringComparison.OrdinalIgnoreCase) ? "brave"
                 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>A profile per browser: one browser cannot open another's profile folder.</summary>
    static string ProfileFor(string browser) =>
        browser.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase)
            ? Paths.BrowserProfile   // where it always was
            : Paths.BrowserProfile + "-" + Path.GetFileNameWithoutExtension(browser).ToLowerInvariant();

    public static async Task<Cdp> StartAsync(CancellationToken ct)
    {
        var browser = FindBrowser() ?? throw new InvalidOperationException("No Chrome, Edge or Brave was found on this computer.");
        var profile = ProfileFor(browser);
        Directory.CreateDirectory(profile);
        var portFile = Path.Combine(profile, "DevToolsActivePort");

        var cdp = new Cdp();
        // a browser left open from earlier is reused rather than fought with
        var port = await ExistingPort(portFile, ct);
        if (port == 0)
        {
            try { File.Delete(portFile); } catch (IOException) { }
            var start = new ProcessStartInfo(browser) { UseShellExecute = false };
            foreach (var arg in new[]
                     {
                         "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
                         "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
                         "--disable-features=msEdgeSidebarV2,msUndersideButton", "--new-window", "about:blank",
                     })
                start.ArgumentList.Add(arg);
            cdp._process = Process.Start(start);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (port == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(150, ct);
                port = await ExistingPort(portFile, ct);
            }
            if (port == 0) throw new InvalidOperationException("The browser started but did not open its control port.");
        }
        cdp.Port = port;
        await cdp.AttachAsync(ct);
        return cdp;
    }

    static async Task<int> ExistingPort(string portFile, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(portFile)) return 0;
            var first = (await File.ReadAllLinesAsync(portFile, ct)).FirstOrDefault();
            if (!int.TryParse(first, out var port)) return 0;
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await Core.Providers.Http.Client.GetAsync("http://127.0.0.1:" + port + "/json/version", probe.Token);
            return response.IsSuccessStatusCode ? port : 0;
        }
        catch (Exception) { return 0; }
    }

    async Task AttachAsync(CancellationToken ct)
    {
        var list = JsonNode.Parse(await Core.Providers.Http.Client.GetStringAsync("http://127.0.0.1:" + Port + "/json/list", ct)) as JsonArray;
        var page = list?.OfType<JsonObject>().FirstOrDefault(t => t["type"]?.GetValue<string>() == "page");
        if (page is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:" + Port + "/json/new?about:blank");
            using var response = await Core.Providers.Http.Client.SendAsync(request, ct);
            page = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        }
        var url = page?["webSocketDebuggerUrl"]?.GetValue<string>() ?? throw new InvalidOperationException("No page to control.");
        await _socket.ConnectAsync(new Uri(url), ct);
        _ = Task.Run(ReceiveLoop);
        await SendAsync("Page.enable", null, ct);
        await SendAsync("Runtime.enable", null, ct);
    }

    async Task ReceiveLoop()
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_closing.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _closing.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var node = JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
                if (node?["id"] is JsonValue id && _pending.TryRemove(id.GetValue<int>(), out var waiter))
                {
                    if (node["error"] is JsonObject error)
                        waiter.TrySetException(new InvalidOperationException(error["message"]?.GetValue<string>() ?? "browser error"));
                    else
                        waiter.TrySetResult(node["result"]);
                }
            }
        }
        catch (Exception) { }
        foreach (var waiter in _pending.Values)
            waiter.TrySetException(new InvalidOperationException("The browser was closed."));
    }

    public bool Alive => _socket.State == WebSocketState.Open;

    public async Task<JsonNode?> SendAsync(string method, JsonObject? parameters, CancellationToken ct, int timeoutSeconds = 30)
    {
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        var payload = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        await _socket.SendAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), WebSocketMessageType.Text, true, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await using (timeout.Token.Register(() => waiter.TrySetException(new TimeoutException(method + " timed out."))))
            return await waiter.Task;
    }

    /// <summary>Runs JavaScript in the page and returns its value.</summary>
    public async Task<JsonNode?> EvalAsync(string expression, CancellationToken ct, int timeoutSeconds = 30)
    {
        var result = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
        }, ct, timeoutSeconds);
        if (result?["exceptionDetails"] is JsonObject failure)
            throw new InvalidOperationException(failure["exception"]?["description"]?.GetValue<string>()
                                                ?? failure["text"]?.GetValue<string>() ?? "script error");
        return result?["result"]?["value"];
    }

    public async Task WaitForLoadAsync(CancellationToken ct, int seconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        await Task.Delay(250, ct);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = (await EvalAsync("document.readyState", ct, 5))?.GetValue<string>();
                if (state is "complete" or "interactive") return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
            await Task.Delay(200, ct);
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
                _socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":999999,\"method\":\"Browser.close\"}"),
                                  WebSocketMessageType.Text, true, CancellationToken.None).Wait(1000);
        }
        catch (Exception) { }
        _socket.Dispose();
        _process?.Dispose();
    }

    public static string Quote(string text) => JsonSerializer.Serialize(text);
}
