namespace MeetJoe.App.Controls;

/// <summary>
/// Holds the in-meeting LiveKit host while Calendar/MeetingStage is unloaded so browsing
/// other sections can show a Teams-style PiP without reconnecting.
/// </summary>
public static class MeetingMediaParking
{
    public static InAppMediaHost? Host { get; private set; }

    public static event Action? Changed;

    public static void Park(InAppMediaHost host)
    {
        if (ReferenceEquals(Host, host)) return;
        Host = host;
        Changed?.Invoke();
    }

    public static InAppMediaHost? Take()
    {
        var host = Host;
        Host = null;
        if (host is not null) Changed?.Invoke();
        return host;
    }

    public static async Task ShutdownAsync()
    {
        var host = Take();
        if (host is null) return;
        try { await host.ShutdownAsync(); }
        catch { /* ignore */ }
    }
}
