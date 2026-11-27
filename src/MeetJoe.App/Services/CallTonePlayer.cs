using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace MeetJoe.App.Services;

/// <summary>Teams-like ringtone (incoming) and ringback (outgoing calling) tones.</summary>
public interface ICallTonePlayer : IDisposable
{
    void PlayIncoming();
    void PlayOutgoing();
    void Stop();
}

/// <summary>
/// Plays the bundled incoming-call MP3 on a loop, and a synthesized ringback for outbound calls.
/// </summary>
public sealed class CallTonePlayer : ICallTonePlayer
{
    private static readonly Uri IncomingAsset =
        new("avares://MeetJoe.App/Assets/Sounds/incoming-call.mp3");

    private readonly object _gate = new();
    private CancellationTokenSource? _loop;
    private string? _incomingPath;
    private string? _outgoingPath;
    private ToneKind _playing;

    private enum ToneKind { None, Incoming, Outgoing }

    public void PlayIncoming() => Start(ToneKind.Incoming);
    public void PlayOutgoing() => Start(ToneKind.Outgoing);

    public void Stop()
    {
        lock (_gate)
        {
            _playing = ToneKind.None;
            CancelLoop();
        }
    }

    public void Dispose()
    {
        Stop();
        TryDelete(_incomingPath);
        TryDelete(_outgoingPath);
        _incomingPath = null;
        _outgoingPath = null;
    }

    private void Start(ToneKind kind)
    {
        lock (_gate)
        {
            if (_playing == kind) return;
            CancelLoop();
            _playing = kind;

            var path = kind == ToneKind.Incoming ? EnsureIncoming() : EnsureOutgoing();
            var cts = new CancellationTokenSource();
            _loop = cts;
            _ = LoopAsync(path, cts.Token);
        }
    }

    private void CancelLoop()
    {
        try { _loop?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
        _loop?.Dispose();
        _loop = null;
    }

    private string EnsureIncoming()
    {
        if (_incomingPath is not null) return _incomingPath;

        var path = Path.Combine(Path.GetTempPath(), $"meetjoe-ring-in-{Guid.NewGuid():N}.mp3");
        using (var src = AssetLoader.Open(IncomingAsset))
        using (var dst = File.Create(path))
            src.CopyTo(dst);

        return _incomingPath = path;
    }

    private string EnsureOutgoing() =>
        _outgoingPath ??= WriteTempWav("meetjoe-ring-out", CallToneWav.BuildOutgoing());

    private static string WriteTempWav(string prefix, byte[] wav)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);
        return path;
    }

    private static async Task LoopAsync(string path, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PlayOnceAsync(path, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Audio helper missing or device busy — don't crash the call UI.
                try { await Task.Delay(1500, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task PlayOnceAsync(string path, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await RunAsync("afplay", ["-q", "1", path], ct);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                await PlayWindowsMp3Async(path, ct);
                return;
            }

            // Avoid System.Media (Windows-only assembly) so this project builds on macOS CI too.
            var escaped = path.Replace("'", "''");
            await RunAsync("powershell",
            [
                "-NoProfile", "-Command",
                $"(New-Object Media.SoundPlayer '{escaped}').PlaySync()"
            ], ct);
            return;
        }

        // Linux: prefer helpers that understand MP3, then WAV-capable players.
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (await TryRunAsync("ffplay", ["-nodisp", "-autoexit", "-loglevel", "quiet", path], ct)) return;
            if (await TryRunAsync("mpg123", ["-q", path], ct)) return;
        }

        if (await TryRunAsync("paplay", [path], ct)) return;
        if (await TryRunAsync("aplay", ["-q", path], ct)) return;
        await Task.Delay(2000, ct);
    }

    /// <summary>WPF MediaPlayer via PowerShell — SoundPlayer cannot decode MP3.</summary>
    private static Task PlayWindowsMp3Async(string path, CancellationToken ct)
    {
        var escaped = path.Replace("'", "''");
        var script =
            "Add-Type -AssemblyName PresentationCore; " +
            "$m = New-Object System.Windows.Media.MediaPlayer; " +
            $"$m.Open([Uri]::new('{escaped}')); " +
            "$m.Play(); " +
            "$sw = [Diagnostics.Stopwatch]::StartNew(); " +
            "while (-not $m.NaturalDuration.HasTimeSpan) { " +
            "  if ($sw.ElapsedMilliseconds -gt 8000) { break }; " +
            "  Start-Sleep -Milliseconds 40 " +
            "}; " +
            "if ($m.NaturalDuration.HasTimeSpan) { " +
            "  $remain = [int]($m.NaturalDuration.TimeSpan.TotalMilliseconds - $sw.ElapsedMilliseconds); " +
            "  if ($remain -gt 0) { Start-Sleep -Milliseconds $remain } " +
            "}; " +
            "$m.Stop(); $m.Close()";
        return RunAsync("powershell", ["-NoProfile", "-Command", script], ct);
    }

    private static async Task<bool> TryRunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            await RunAsync(fileName, args, ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                /* ignore */
            }

            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with {process.ExitCode}.");
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception) { /* temp cleanup best-effort */ }
    }
}

/// <summary>No-op player for headless UI tests.</summary>
public sealed class NullCallTonePlayer : ICallTonePlayer
{
    public void PlayIncoming() { }
    public void PlayOutgoing() { }
    public void Stop() { }
    public void Dispose() { }
}

/// <summary>Builds a compact PCM WAV loop for outbound ringback.</summary>
internal static class CallToneWav
{
    private const int SampleRate = 44_100;

    /// <summary>Outgoing: classic dual-tone ringback (~2s on, ~4s off).</summary>
    public static byte[] BuildOutgoing()
    {
        var samples = new List<float>(SampleRate * 6);
        AddDualTone(samples, 440f, 480f, 2.0, 0.32f);
        AddSilence(samples, 4.0);
        return ToWav(samples);
    }

    private static void AddDualTone(List<float> samples, float a, float b, double seconds, float gain)
    {
        var count = (int)(SampleRate * seconds);
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            var env = Envelope(i, count, 0.02, 0.05);
            var sample = (Math.Sin(2 * Math.PI * a * t) + Math.Sin(2 * Math.PI * b * t)) * 0.5;
            samples.Add((float)(sample * gain * env));
        }
    }

    private static void AddSilence(List<float> samples, double seconds)
    {
        var count = (int)(SampleRate * seconds);
        for (var i = 0; i < count; i++) samples.Add(0f);
    }

    private static double Envelope(int i, int count, double attack, double release)
    {
        var t = i / (double)SampleRate;
        var dur = count / (double)SampleRate;
        var a = Math.Clamp(t / attack, 0, 1);
        var r = Math.Clamp((dur - t) / release, 0, 1);
        return a * r;
    }

    private static byte[] ToWav(List<float> samples)
    {
        var dataSize = samples.Count * 2;
        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataSize);
        foreach (var s in samples)
        {
            var v = (short)Math.Clamp((int)(s * short.MaxValue), short.MinValue, short.MaxValue);
            w.Write(v);
        }

        return ms.ToArray();
    }
}
