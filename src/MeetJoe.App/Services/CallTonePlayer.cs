using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MeetJoe.App.Services;

/// <summary>Teams-like ringtone (incoming) and ringback (outgoing calling) tones.</summary>
public interface ICallTonePlayer : IDisposable
{
    void PlayIncoming();
    void PlayOutgoing();
    void Stop();
}

/// <summary>Synthesizes short WAV loops and plays them with the OS audio helper.</summary>
public sealed class CallTonePlayer : ICallTonePlayer
{
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

    private string EnsureIncoming() =>
        _incomingPath ??= WriteTempWav("meetjoe-ring-in", CallToneWav.BuildIncoming());

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
            // Avoid System.Media (Windows-only assembly) so this project builds on macOS CI too.
            var escaped = path.Replace("'", "''");
            await RunAsync("powershell",
            [
                "-NoProfile", "-Command",
                $"(New-Object Media.SoundPlayer '{escaped}').PlaySync()"
            ], ct);
            return;
        }

        // Linux: PulseAudio / ALSA helpers when present.
        if (await TryRunAsync("paplay", [path], ct)) return;
        if (await TryRunAsync("aplay", ["-q", path], ct)) return;
        await Task.Delay(2000, ct);
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

/// <summary>Builds compact PCM WAV loops that feel like Teams ring / ringback.</summary>
internal static class CallToneWav
{
    private const int SampleRate = 44_100;

    /// <summary>Incoming: bright two-burst chime, then a short pause (loops).</summary>
    public static byte[] BuildIncoming()
    {
        var samples = new List<float>(SampleRate * 4);
        // Burst 1 — ascending pair
        AddTone(samples, 784f, 0.14, 0.42f); // G5
        AddSilence(samples, 0.06);
        AddTone(samples, 988f, 0.18, 0.38f); // B5
        AddSilence(samples, 0.28);
        // Burst 2 — echo softer
        AddTone(samples, 784f, 0.12, 0.28f);
        AddSilence(samples, 0.05);
        AddTone(samples, 988f, 0.16, 0.24f);
        AddSilence(samples, 1.55);
        return ToWav(samples);
    }

    /// <summary>Outgoing: classic dual-tone ringback (~2s on, ~4s off).</summary>
    public static byte[] BuildOutgoing()
    {
        var samples = new List<float>(SampleRate * 6);
        AddDualTone(samples, 440f, 480f, 2.0, 0.32f);
        AddSilence(samples, 4.0);
        return ToWav(samples);
    }

    private static void AddTone(List<float> samples, float hz, double seconds, float gain)
    {
        var count = (int)(SampleRate * seconds);
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            var env = Envelope(i, count, 0.012, 0.04);
            samples.Add((float)(Math.Sin(2 * Math.PI * hz * t) * gain * env));
        }
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
