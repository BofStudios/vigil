using System.Text.Json;
using NAudio.CoreAudioApi;
using Vigil.Core;

namespace Vigil.Voice;

/// <summary>While you hold Ctrl+&lt; and talk, and while Vigil answers, whatever else is playing - music, a
/// video - drops to a murmur, then comes back. Only other apps' own volume is touched, never the system
/// volume, and only if it is still where Vigil left it: a volume changed by hand in between is the
/// person's, and stays. What it was is written down first, so a Vigil that closed mid-sentence puts it
/// back the next time it starts.</summary>
public static class Ducker
{
    /// <summary>A sixth of what it was: still there, no longer in the way.</summary>
    const float Factor = 0.16f;

    static readonly object Gate = new();
    static readonly HashSet<string> Reasons = new();
    static Dictionary<string, (float Was, float Set)>? _ducked;
    static int _queued;

    static string StateFile => Path.Combine(Paths.Home, "ducked.json");

    /// <summary>Something wants quiet (on) or no longer does (off): "talk" while the key is held, "speak"
    /// while Vigil speaks. Others stay low until neither wants it. Never waits for the sound system.</summary>
    public static void Want(string reason, bool on)
    {
        lock (Gate)
        {
            if (on) Reasons.Add(reason);
            else Reasons.Remove(reason);
        }
        // the latest wish wins, whatever order the work runs in
        if (Interlocked.Exchange(ref _queued, 1) == 0)
            _ = Task.Run(Apply);
    }

    static void Apply()
    {
        Interlocked.Exchange(ref _queued, 0);
        lock (Gate)
        {
            var want = Reasons.Count > 0;
            try
            {
                if (want && _ducked is null) Duck();
                else if (!want && _ducked is not null) Restore();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                _ducked = want ? new() : null;   // no sound device to speak of: nothing to put back
            }
        }
    }

    static void Duck()
    {
        var ducked = new Dictionary<string, (float Was, float Set)>();
        var self = (uint)Environment.ProcessId;
        ForEachSession(session =>
        {
            if (session.IsSystemSoundsSession || session.GetProcessID == self) return;
            if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) return;
            var was = session.SimpleAudioVolume.Volume;
            if (was < 0.02f) return;
            var set = was * Factor;
            Ramp(session.SimpleAudioVolume, was, set);
            ducked[session.GetSessionInstanceIdentifier] = (was, set);
        });
        _ducked = ducked;
        Remember(ducked);
    }

    static void Restore()
    {
        var ducked = _ducked ?? new();
        _ducked = null;
        Put(ducked);
        Remember(null);
    }

    /// <summary>Back to what it was - each session still at the level Vigil set, and no other.</summary>
    static void Put(Dictionary<string, (float Was, float Set)> ducked)
    {
        if (ducked.Count == 0) return;
        ForEachSession(session =>
        {
            if (!ducked.TryGetValue(session.GetSessionInstanceIdentifier, out var level)) return;
            var now = session.SimpleAudioVolume.Volume;
            if (Math.Abs(now - level.Set) > 0.02f) return;   // changed by hand since: theirs now
            Ramp(session.SimpleAudioVolume, now, level.Was);
        });
    }

    /// <summary>A sound that was left low by a Vigil that closed in the middle: put back at start.</summary>
    public static void PutBackLeftovers()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(StateFile));
            File.Delete(StateFile);
            if (saved is null) return;
            lock (Gate) Put(saved.Where(p => p.Value.Length == 2).ToDictionary(p => p.Key, p => (p.Value[0], p.Value[1])));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Puts everything back now - when Vigil closes.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            Reasons.Clear();
            try { if (_ducked is not null) Restore(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        }
    }

    static void Remember(Dictionary<string, (float Was, float Set)>? ducked)
    {
        try
        {
            if (ducked is null || ducked.Count == 0)
            {
                if (File.Exists(StateFile)) File.Delete(StateFile);
                return;
            }
            Directory.CreateDirectory(Paths.Home);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(ducked.ToDictionary(p => p.Key, p => new[] { p.Value.Was, p.Value.Set })));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A short slide rather than a jump, so it sounds like a hand on the dial.</summary>
    static void Ramp(SimpleAudioVolume volume, float from, float to)
    {
        const int steps = 6;
        for (var i = 1; i <= steps; i++)
        {
            volume.Volume = from + (to - from) * i / steps;
            if (i < steps) Thread.Sleep(20);
        }
    }

    static void ForEachSession(Action<AudioSessionControl> act)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            try { act(session); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { }
            finally { session.Dispose(); }
        }
    }
}
