using System.Diagnostics;
using System.Text.Json.Nodes;
using Vigil.Desktop;

namespace Vigil.Tests;

/// <summary>Vigil's own browser, really started: it must come up, take orders and load a page.
/// Opens a browser window, so it runs only with VIGIL_LIVE=1.</summary>
public class BrowserLiveTests
{
    static bool Live => Environment.GetEnvironmentVariable("VIGIL_LIVE") == "1";

    [Fact]
    public async Task The_browser_starts_and_loads_a_page()
    {
        if (!Live) return;
        var watch = Stopwatch.StartNew();
        using var cdp = await Cdp.StartAsync(CancellationToken.None);
        var started = watch.Elapsed;
        await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = "https://example.com" }, CancellationToken.None);
        await Task.Delay(2500);
        var title = await cdp.SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = "document.title", ["returnByValue"] = true }, CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-browser-live.txt"),
            "browser " + Cdp.FindBrowser() + "\nstarted in " + started.TotalSeconds.ToString("0.0") + " s\ntitle " + title?.ToJsonString());
        // close the window it opened: left running, it holds the test run open with it
        foreach (var pidFile in Directory.GetFiles(Path.GetDirectoryName(Vigil.Core.Paths.BrowserProfile)!, "vigil.pid", SearchOption.AllDirectories))
            try { using var p = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))); p.Kill(entireProcessTree: true); }
            catch (Exception) { }
        Assert.True(cdp.Port > 0);
        Assert.Contains("Example", title?["result"]?["value"]?.GetValue<string>() ?? "");
    }
}
