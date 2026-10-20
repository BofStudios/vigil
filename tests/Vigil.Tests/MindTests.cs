using Vigil.Core;

namespace Vigil.Tests;

[Collection("mind")]
public class MindTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vigil-mind-" + Guid.NewGuid().ToString("N"));

    public MindTests() => Mind.UseRoot(_root);

    public void Dispose()
    {
        Mind.UseRoot(null);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void The_folder_explains_itself()
    {
        Mind.Ensure();
        Assert.True(File.Exists(Path.Combine(_root, "README.txt")));
        Assert.True(Directory.Exists(Mind.NotesDir));
        Assert.True(Directory.Exists(Mind.JournalDir));
    }

    [Fact]
    public void The_journal_keeps_a_timed_line_per_event()
    {
        Mind.Journal("asked", "open   Spotify\nplease");
        Mind.Journal("did", "open: Spotify");
        var lines = Mind.JournalLines(DateTime.Now);
        Assert.Equal(2, lines.Count);
        Assert.Contains("**asked**  open Spotify please", lines[0]);
        Assert.Matches(@"^- \d\d:\d\d ", lines[1]);
    }

    [Fact]
    public void Notes_are_plain_files_that_can_be_found_again()
    {
        Mind.WriteNote("Shopping list", "milk\neggs", append: false);
        Mind.WriteNote("Shopping list", "coffee", append: true);
        var body = Mind.ReadNote("shopping");
        Assert.NotNull(body);
        Assert.Contains("milk", body);
        Assert.Contains("coffee", body);
        Assert.Single(Mind.SearchNotes("coffee"));
        Assert.True(File.Exists(Path.Combine(Mind.NotesDir, "Shopping list.md")));
    }

    [Fact]
    public void A_title_cannot_escape_the_notes_folder()
    {
        var file = Mind.NoteFile(@"..\..\Windows\evil:name?");
        Assert.Equal(Mind.NotesDir, Path.GetDirectoryName(file));
    }

    [Theory]
    [InlineData(30, "<1m")]
    [InlineData(600, "10m")]
    [InlineData(7500, "2h 05m")]
    public void Durations_read_like_a_person_would_say_them(double seconds, string expected) =>
        Assert.Equal(expected, Activity.Duration(seconds));

    [Fact]
    public void A_check_in_always_has_something_to_say()
    {
        var back = CheckIn.Fallback(new CheckIn.Moment("back", "Visual Studio Code", TimeSpan.FromMinutes(40)));
        var long_ = CheckIn.Fallback(new CheckIn.Moment("streak", "Visual Studio Code", TimeSpan.FromHours(3)));
        Assert.EndsWith("?", back);
        Assert.Contains("Visual Studio Code", long_);
        Assert.EndsWith("?", long_);
    }
}
