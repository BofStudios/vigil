using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>Synthetic rooms: a little hiss, and claps, speech or typing laid over it.</summary>
public class ClapTests
{
    const int Rate = 16000;

    static short[] Room(double seconds, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new short[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)random.Next(-120, 121);
        return samples;
    }

    /// <summary>A clap: a burst of noise at full strength that dies away in about 40 ms.</summary>
    static void Clap(short[] room, double at, double strength = 22000, int seed = 7)
    {
        var random = new Random(seed + (int)(at * 1000));
        var start = (int)(at * Rate);
        for (var i = 0; i < Rate / 8 && start + i < room.Length; i++)
        {
            var envelope = Math.Exp(-i / (Rate * 0.012));
            room[start + i] = (short)Math.Clamp(room[start + i] + (random.NextDouble() * 2 - 1) * strength * envelope, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>Speech: a loud sound that carries on for a few hundred milliseconds.</summary>
    static void Talk(short[] room, double at, double seconds)
    {
        var start = (int)(at * Rate);
        for (var i = 0; i < seconds * Rate && start + i < room.Length; i++)
        {
            var voice = Math.Sin(2 * Math.PI * 180 * i / Rate) * 9000 * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 4 * i / Rate));
            room[start + i] = (short)Math.Clamp(room[start + i] + voice, short.MinValue, short.MaxValue);
        }
    }

    static int Wakes(short[] room)
    {
        var detector = new ClapDetector();
        var wakes = 0;
        detector.TripleClap += () => wakes++;
        var bytes = new byte[room.Length * 2];
        Buffer.BlockCopy(room, 0, bytes, 0, bytes.Length);
        // fed the way the microphone delivers it: 30 ms at a time
        for (var offset = 0; offset < bytes.Length; offset += 960)
            detector.Feed(bytes.AsSpan(offset, Math.Min(960, bytes.Length - offset)));
        return wakes;
    }

    [Fact]
    public void Three_claps_wake_it_once()
    {
        var room = Room(4);
        Clap(room, 1.0); Clap(room, 1.45); Clap(room, 1.9);
        Assert.Equal(1, Wakes(room));
    }

    [Fact]
    public void Three_slower_claps_still_count()
    {
        var room = Room(5);
        Clap(room, 1.0); Clap(room, 1.75); Clap(room, 2.5);
        Assert.Equal(1, Wakes(room));
    }

    [Fact]
    public void Two_claps_do_not()
    {
        var room = Room(4);
        Clap(room, 1.0); Clap(room, 1.45);
        Assert.Equal(0, Wakes(room));
    }

    [Fact]
    public void Applause_does_not()
    {
        var room = Room(5);
        for (var i = 0; i < 8; i++) Clap(room, 1.0 + i * 0.3);
        Assert.Equal(0, Wakes(room));
    }

    [Fact]
    public void Uneven_knocks_do_not()
    {
        var room = Room(5);
        Clap(room, 1.0); Clap(room, 1.15); Clap(room, 1.95);
        Assert.Equal(0, Wakes(room));
    }

    [Fact]
    public void Talking_does_not()
    {
        var room = Room(5);
        Talk(room, 0.8, 0.35); Talk(room, 1.3, 0.3); Talk(room, 1.8, 0.4); Talk(room, 2.4, 0.25);
        Assert.Equal(0, Wakes(room));
    }

    [Fact]
    public void Quiet_typing_does_not()
    {
        var room = Room(5);
        for (var i = 0; i < 3; i++) Clap(room, 1.0 + i * 0.4, strength: 2500);
        Assert.Equal(0, Wakes(room));
    }

    [Fact]
    public void Claps_right_after_talking_do_not()
    {
        var room = Room(5);
        Talk(room, 0.6, 0.6);
        Clap(room, 1.25); Clap(room, 1.7); Clap(room, 2.15);
        Assert.Equal(0, Wakes(room));
    }
}
