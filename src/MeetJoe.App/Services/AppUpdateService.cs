using System.Reflection;
using MeetJoe.Contracts;

namespace MeetJoe.App.Services;

public enum AppUpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Updating,
    Unsupported,
    Failed,
}

public sealed class AppUpdateStatus
{
    public AppUpdateState State { get; init; } = AppUpdateState.Idle;
    public string CurrentVersion { get; init; } = "0.0.0";
    public string? AvailableVersion { get; init; }
    public string? Message { get; init; }
    public int DownloadPercent { get; init; }
}

/// <summary>Checks a semver update feed and applies in-place updates for installed desktop builds.</summary>
public interface IAppUpdateService
{
    bool IsInstalledBuild { get; }

    AppUpdateStatus Status { get; }

    event Action? StatusChanged;

    /// <summary>Check the feed; when <paramref name="applyAutomatically"/> is true, download and restart.</summary>
    Task CheckAsync(bool applyAutomatically = true, CancellationToken ct = default);
}

public sealed class NullAppUpdateService : IAppUpdateService
{
    public bool IsInstalledBuild => false;

    public AppUpdateStatus Status { get; } = new()
    {
        State = AppUpdateState.Unsupported,
        CurrentVersion = AppVersion.Current,
        Message = "Auto-update is available for installed MeetJoe builds.",
    };

    public event Action? StatusChanged
    {
        add { }
        remove { }
    }

    public Task CheckAsync(bool applyAutomatically = true, CancellationToken ct = default) => Task.CompletedTask;
}

public static class AppVersion
{
    public static string Current
    {
        get
        {
            var asm = typeof(AppVersion).Assembly;
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString(3)
                ?? "0.0.0";
            return informational.Split('+', 2)[0].Trim();
        }
    }
}
