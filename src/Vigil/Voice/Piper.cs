using System.Diagnostics;
using Vigil.Core;

namespace Vigil.Voice;

/// <summary>A neural voice that runs on this computer: Piper (MIT licensed, no cloud, no key).
///
/// Nothing is bundled. Put piper.exe and a voice (an .onnx file with its .onnx.json) in
/// `~/.vigil/voices/piper` and Vigil finds them. The process stays running with the voice
/// loaded, so a sentence costs a fraction of a second instead of a model load.</summary>
public sealed class PiperVoice : IDisposable
{
    public static string Folder => Path.Combine(Paths.Home, "voices", "piper");

    /// <summary>piper.exe, wherever the archive put it inside the folder.</summary>
    public static string? Exe()
    {
        if (!Directory.Exists(Folder)) return null;
        try { return Directory.EnumerateFiles(Folder, "piper.exe", SearchOption.AllDirectories).FirstOrDefault(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The voice to use: the one named in settings, else the first one found.</summary>
    public static string? Model(string preferred)
    {
        if (!Directory.Exists(Folder)) return null;
        string[] models;
        try { models = Directory.EnumerateFiles(Folder, "*.onnx", SearchOption.AllDirectories).Where(m => File.Exists(m + ".json")).ToArray(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (models.Length == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var named = models.FirstOrDefault(m => Path.GetFileName(m).Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;
        }
        return models.OrderBy(m => Path.GetFileName(m)).First();
    }

    public static bool Installed(string preferred) => Exe() is not null && Model(preferred) is not null;

    /// <summary>"en_GB-alan-medium.onnx" -> "en": the language a voice can speak.</summary>
    public static string LanguageOf(string model)
    {
        var name = Path.GetFileName(model);
        var cut = name.IndexOf('_');
        return cut > 0 ? name[..cut].ToLowerInvariant() : "en";
    }

    readonly SemaphoreSlim _one = new(1, 1);
    Process? _process;
    string _loaded = "";
    string _scale = "";
    string _scratch = "";

    public async Task<byte[]> SpeakAsync(string text, string preferredModel, double rate, CancellationToken ct)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (line.Length == 0) return Array.Empty<byte>();
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await SpeakOnceAsync(line, preferredModel, rate, ct).ConfigureAwait(false); }
                catch (Exception ex) when (attempt == 1 && ex is IOException or InvalidOperationException or TimeoutException)
                {
                    Kill();   // a stuck or crashed process: start a fresh one and try once more
                }
            }
        }
        finally { _one.Release(); }
    }

    async Task<byte[]> SpeakOnceAsync(string line, string preferredModel, double rate, CancellationToken ct)
    {
        var exe = Exe() ?? throw new InvalidOperationException("piper.exe was not found.");
        var model = Model(preferredModel) ?? throw new InvalidOperationException("No Piper voice was found.");
        // length_scale is how long each sound lasts: below 1 is faster
        var scale = (1.0 / Math.Clamp(rate, 0.5, 2.0)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        if (_process is null || _process.HasExited || _loaded != model || _scale != scale)
        {
            Kill();
            _scratch = Path.Combine(Path.GetTempPath(), "vigil-piper-" + Environment.ProcessId);
            Directory.CreateDirectory(_scratch);
            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "--model", model, "--output_dir", _scratch, "--length_scale", scale, "--sentence_silence", "0.12" })
                start.ArgumentList.Add(arg);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Piper did not start.");
            _process.ErrorDataReceived += (_, _) => { };   // progress chatter; draining it keeps Piper from blocking
            _process.BeginErrorReadLine();
            _loaded = model;
            _scale = scale;
        }

        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string? path;
        try { path = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Piper took too long."); }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new IOException("Piper made no audio.");

        try { return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        Kill();
        try { if (_scratch.Length > 0) Directory.Delete(_scratch, true); } catch (IOException) { }
        _one.Dispose();
    }
}
