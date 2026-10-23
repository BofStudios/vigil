using Vigil.Desktop;

namespace Vigil.Tests;

/// <summary>"Open Paint" has to work: Store apps have no .exe and no shortcut, and on a Turkish
/// Windows the calculator is called Hesap Makinesi.</summary>
public class StartAppsTests
{
    static readonly StartApps.App[] Apps =
    {
        new("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App"),
        new("Hesap Makinesi", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        new("Not Defteri", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"),
        new("WhatsApp", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"),
        new("Spotify", @"C:\Users\Pc\AppData\Roaming\Spotify\Spotify.exe"),
        new("Google Chrome", "Chrome"),
        new("Steam Support Center", "http://support.steampowered.com/"),
        new("Uninstall Node.js", @"C:\Program Files\nodejs\uninstall.exe"),
        new("Node.js", @"C:\Program Files\nodejs\node.exe"),
    };

    [Theory]
    [InlineData("Paint", "Paint")]
    [InlineData("paint", "Paint")]
    [InlineData("calculator", "Hesap Makinesi")]
    [InlineData("Hesap Makinesi", "Hesap Makinesi")]
    [InlineData("notepad", "Not Defteri")]
    [InlineData("WhatsApp", "WhatsApp")]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("spotify", "Spotify")]
    [InlineData("node", "Node.js")]
    public void A_name_finds_the_app(string asked, string expected) => Assert.Equal(expected, StartApps.Find(asked, Apps)?.Name);

    [Theory]
    [InlineData("Acer Gamers TV")]
    [InlineData("")]
    public void Nothing_close_is_nothing(string asked) => Assert.Null(StartApps.Find(asked, Apps));

    [Fact]
    public void Never_an_uninstaller_or_a_help_link()
    {
        Assert.NotEqual("Uninstall Node.js", StartApps.Find("node", Apps)?.Name);
        Assert.Null(StartApps.Find("steam support", Apps));
    }

    [Fact]
    public void This_computer_has_its_start_menu()
    {
        var all = StartApps.All();
        if (all.Count == 0) return;   // a machine without a shell, e.g. a build server
        Assert.NotNull(StartApps.Find("paint", all));
    }
}
