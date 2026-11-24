using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using MeetJoe.App.Services;
using MeetJoe.Contracts;
using NuGet.Versioning;
using Velopack;

namespace MeetJoe.Desktop;

/// <summary>
/// Semver auto-update via Velopack. Downloads deltas when available, otherwise the full package,
/// then restarts into the new version — no full reinstall from a DMG/MSI.
/// </summary>
public sealed class VelopackAppUpdateService(AppConfig config) : IAppUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private AppUpdateStatus _status = new()
    {
        State = AppUpdateState.Idle,
        CurrentVersion = AppVersion.Current,
    };
    private int _checkGate;

    public bool IsInstalledBuild
    {
        get
        {
            try { return new UpdateManager(config.ApiBaseUri.ToString()).IsInstalled; }
            catch { return false; }
        }
    }

    public AppUpdateStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event Action? StatusChanged;

    public async Task CheckAsync(bool applyAutomatically = true, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _checkGate, 1, 0) != 0) return;
        try
        {
            SetStatus(AppUpdateState.Checking, message: "Checking for updates…");

            var rid = CurrentRid();
            DesktopUpdateInfoDto? manifest = null;
            try
            {
                manifest = await FetchManifestAsync(rid, ct);
            }
            catch (Exception ex)
            {
                // Fall through to Velopack feed derived from API base when manifest is missing.
                SetStatus(AppUpdateState.Checking, message: $"Update catalog: {ex.Message}");
            }

            var feedUrl = manifest?.FeedUrl
                ?? new Uri(config.ApiBaseUri, $"updates/desktop/{rid}/").ToString();

            UpdateManager mgr;
            try
            {
                mgr = new UpdateManager(feedUrl);
            }
            catch (Exception ex)
            {
                SetStatus(AppUpdateState.Failed, message: ex.Message);
                return;
            }

            if (!mgr.IsInstalled)
            {
                var remote = manifest?.Version;
                if (remote is not null
                    && NuGetVersion.TryParse(AppVersion.Current, out var local)
                    && NuGetVersion.TryParse(remote, out var latest)
                    && latest > local)
                {
                    SetStatus(
                        AppUpdateState.Unsupported,
                        available: remote,
                        message: $"Version {remote} is available. Install from meetjoe.joesolutions.ph once — then Check for updates applies newer builds in place.");
                }
                else
                {
                    SetStatus(
                        AppUpdateState.Unsupported,
                        message: "This copy was not installed via the MeetJoe updater. Install from meetjoe.joesolutions.ph once; later versions update in place (no full re-download).");
                }
                return;
            }

            UpdateInfo? update;
            try
            {
                update = await mgr.CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                SetStatus(AppUpdateState.Failed, message: ex.Message);
                return;
            }

            if (update is null)
            {
                SetStatus(AppUpdateState.UpToDate, message: "You're on the latest version.");
                return;
            }

            var available = update.TargetFullRelease.Version.ToString();
            SetStatus(AppUpdateState.Available, available, $"Version {available} is available.");

            if (!applyAutomatically) return;

            SetStatus(AppUpdateState.Downloading, available, "Downloading update…", 0);
            try
            {
                await mgr.DownloadUpdatesAsync(update, p =>
                    SetStatus(AppUpdateState.Downloading, available, $"Downloading update… {p}%", p));
            }
            catch (Exception ex)
            {
                SetStatus(AppUpdateState.Failed, available, ex.Message);
                return;
            }

            SetStatus(AppUpdateState.Updating, available, "Installing update and restarting…", 100);
            mgr.ApplyUpdatesAndRestart(update);
        }
        finally
        {
            Interlocked.Exchange(ref _checkGate, 0);
        }
    }

    private async Task<DesktopUpdateInfoDto?> FetchManifestAsync(string rid, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = config.ApiBaseUri, Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.GetAsync($"api/desktop/updates?rid={Uri.EscapeDataString(rid)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<DesktopUpdateInfoDto>(stream, JsonOptions, ct);
    }

    private void SetStatus(
        AppUpdateState state,
        string? available = null,
        string? message = null,
        int percent = 0)
    {
        lock (_gate)
        {
            _status = new AppUpdateStatus
            {
                State = state,
                CurrentVersion = AppVersion.Current,
                AvailableVersion = available,
                Message = message,
                DownloadPercent = percent,
            };
        }

        if (Dispatcher.UIThread.CheckAccess())
            StatusChanged?.Invoke();
        else
            Dispatcher.UIThread.Post(() => StatusChanged?.Invoke());
    }

    internal static string CurrentRid()
    {
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        return RuntimeInformation.RuntimeIdentifier;
    }
}
