using Vigil.Core;

namespace Vigil.Tests;

/// <summary>Do not disturb: while it is on Vigil does not check in, and a timed one ends by itself.</summary>
public class QuietTests
{
    [Fact]
    public void Off_by_default() => Assert.False(new Config().Quiet);

    [Fact]
    public void On_until_turned_off()
    {
        var c = new Config { DoNotDisturb = true };
        Assert.True(c.Quiet);
    }

    [Fact]
    public void A_timed_one_runs_out_by_itself()
    {
        Assert.True(new Config { DoNotDisturb = true, QuietUntil = DateTime.Now.AddMinutes(30) }.Quiet);
        Assert.False(new Config { DoNotDisturb = true, QuietUntil = DateTime.Now.AddMinutes(-1) }.Quiet);
    }

    [Fact]
    public void While_quiet_there_is_never_a_check_in()
    {
        var c = new Config { Awareness = true, CheckIns = true, CheckInMinutes = 10, DoNotDisturb = true };
        var checkIn = new CheckIn(c);
        Assert.Null(checkIn.Due());
    }
}
