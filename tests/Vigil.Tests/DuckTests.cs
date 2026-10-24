using System.Diagnostics;
using NAudio.CoreAudioApi;
using Vigil.Core;
using Vigil.Voice;

namespace Vigil.Tests;

/// <summary>Other apps' sound steps back while you talk, and comes back after - on the real sound device,
/// with a real second app playing (silence, so nobody hears the test). VIGIL_AUDIO=1 only.</summary>
public class DuckTests
{
    static float? VolumeOf(int pid)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.GetProcessID == pid) return session.SimpleAudioVolume.Volume;
        }
        return null;
    }

    [Fact]
    public async Task Another_apps_sound_drops_while_talking_and_comes_back()
    {
        if (Environment.GetEnvironmentVariable("VIGIL_AUDIO") != "1") return;
        var home = Path.Combine(Path.GetTempPath(), "vigil-duck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var wav = Path.Combine(home, "silence.wav");
        File.WriteAllBytes(wav, ElevenLabs.Wav(new byte[24000 * 2 * 3], 24000));   // three seconds of silence

        var script = "$p = New-Object System.Media.SoundPlayer '" + wav + "'; $p.PlayLooping(); Start-Sleep -Seconds 20";
        using var player = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + script + "\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        try
        {
            float? before = null;
            for (var i = 0; i < 40 && before is null; i++)
            {
                await Task.Delay(150);
                before = VolumeOf(player.Id);
            }
            Assert.NotNull(before);

            Ducker.Want("test", true);
            await Task.Delay(900);
            var ducked = VolumeOf(player.Id);

            Ducker.Want("test", false);
            await Task.Delay(900);
            var after = VolumeOf(player.Id);

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-duck.txt"), $"before {before} ducked {ducked} after {after}");
            Assert.InRange(ducked!.Value, before!.Value * 0.1f, before.Value * 0.25f);
            Assert.InRange(after!.Value, before.Value - 0.02f, before.Value + 0.02f);
        }
        finally
        {
            Ducker.Release();
            try { player.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }
}
