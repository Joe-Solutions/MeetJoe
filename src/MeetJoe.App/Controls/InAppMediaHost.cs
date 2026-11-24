using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MeetJoe.App.Controls;

/// <summary>
/// Lazily hosts the LiveKit WebView. Constructed only when media is open so section
/// pages (Calls, Calendar) stay usable when the WebView runtime is unavailable.
/// </summary>
public sealed class InAppMediaHost : Panel
{
    private IMediaWebView? _webView;
    private Uri? _url;
    private Uri? _navigatedUrl;
    private Action? _onLeave;
    private bool _pageReady;
    private string? _pendingScript;
    private string? _preferredMediaScript;

    public bool IsCameraEnabled { get; private set; } = true;
    public bool IsMicEnabled { get; private set; } = true;
    public bool IsScreenShareEnabled { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsAvailable => _webView is not null;

    private TaskCompletionSource<Guid?>? _recordingStopTcs;
    private string? _recordingUploadScript;

    /// <summary>
    /// Seed preferred cam/mic before LiveKit connect (e.g. after minimize → return reconnects).
    /// </summary>
    public void ApplyPreferredMedia(bool cameraOn, bool micOn)
    {
        IsCameraEnabled = cameraOn;
        IsMicEnabled = micOn;
    }

    public event Action? MediaStateChanged;
    public event Action? MediaReady;
    public event Action<IReadOnlyList<Guid>>? ActiveSpeakersChanged;
    public event Action<IReadOnlyList<Guid>>? MutedParticipantsChanged;
    public event Action<Guid>? RecordingReady;
    public event Action<string>? RecordingFailed;
    /// <summary>Remote host started/stopped recording: (active, displayName, startedAtUnixMs).</summary>
    public event Action<bool, string?, long?>? RoomRecordingChanged;
    /// <summary>Local recorder elapsed-timer anchor (unix ms) or null when stopped.</summary>
    public event Action<long?>? RecordingTimerChanged;
    /// <summary>Shared session clock started (unix ms). 1 hour limit from this instant.</summary>
    public event Action<long>? SessionStarted;
    /// <summary>Five minutes remaining in the session.</summary>
    public event Action? SessionWarning;
    /// <summary>Session hit the 1 hour limit — host should leave/hang up.</summary>
    public event Action? SessionExpired;

    /// <summary>Raised when the page reports OS-level mic/camera permission is blocked.</summary>
    public event Action<string>? PermissionNeeded;

    private readonly HashSet<string> _permissionAlerted = new(StringComparer.OrdinalIgnoreCase);

    public InAppMediaHost()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        // ClipToBounds clips the native WKWebView surface on macOS → blank stage.
        ClipToBounds = false;
        Background = Brushes.Transparent;
        MinWidth = 160;
        MinHeight = 120;
    }

    public void SetLeaveHandler(Action? onLeave) => _onLeave = onLeave;

    public void Show(Uri url)
    {
        var changed = _url != url;
        _url = url;
        EnsureWebView();
        if (_webView is null) return;

        if (changed || _navigatedUrl != url)
        {
            _pageReady = false;
            // TCC dialogs must run before LiveKit getUserMedia or macOS never prompts.
            _ = ShowAfterOsMediaAsync(url);
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    private async Task ShowAfterOsMediaAsync(Uri url)
    {
        // Teams model: ask the OS for mic, then camera, *before* the WebView
        // connects to LiveKit. That is what registers MeetJoe under
        // System Settings → Privacy → Microphone / Camera.
        if (WebViewPlatformHooks.RequestOsMediaPermissionAsync is { } requestOs)
        {
            try
            {
                var micOk = await requestOs("mic").ConfigureAwait(true);
                var camOk = await requestOs("camera").ConfigureAwait(true);
                if (!micOk) RaisePermissionNeeded("mic");
                if (!camOk) RaisePermissionNeeded("camera");
            }
            catch
            {
                // Still navigate — the page banners if getUserMedia fails.
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_webView is null || _url != url) return;
            _navigatedUrl = url;
            _webView.Navigate(url);
        });
    }

    public void Hide() => _ = ShutdownAsync();

    /// <summary>
    /// Stop LiveKit capture and dispose the native WebView before the window tears it down.
    /// Skipping this on macOS often segfaults WKWebView (crash dialog on quit).
    /// </summary>
    public async Task ShutdownAsync()
    {
        _url = null;
        _navigatedUrl = null;
        _pageReady = false;
        _pendingScript = null;
        _preferredMediaScript = null;
        _permissionAlerted.Clear();

        var web = _webView;
        _webView = null;
        if (web is null)
        {
            Children.Clear();
            return;
        }

        web.MessageReceived -= OnMediaMessage;
        web.NavigationCompleted -= OnNavigationCompleted;

        try
        {
            await web.ExecuteScriptAsync(
                "(async function(){ try { if (window.meetjoeShutdown) await window.meetjoeShutdown(); } catch(e){} })()");
        }
        catch { /* page may already be gone */ }

        try { await web.ShutdownAsync(); }
        catch { /* ignore */ }

        Children.Clear();
    }

    public Task ToggleCameraAsync() => ToggleAndSyncAsync(mic: false);

    public Task ToggleMicAsync() => ToggleAndSyncAsync(mic: true);

    public Task ToggleScreenShareAsync() => ToggleScreenShareCoreAsync();

    /// <summary>
    /// Push upload credentials then start MediaRecorder in the LiveKit page.
    /// </summary>
    public async Task<bool> StartRecordingAsync(string uploadUrl, string accessToken, string fileName)
    {
        EnsureWebView();
        if (_webView is null) return false;

        var urlJson = System.Text.Json.JsonSerializer.Serialize(uploadUrl);
        var tokenJson = System.Text.Json.JsonSerializer.Serialize(accessToken);
        var nameJson = System.Text.Json.JsonSerializer.Serialize(fileName);
        _recordingUploadScript =
            $"(function(){{ window.meetjoeRecordingUpload={{url:{urlJson},token:{tokenJson},fileName:{nameJson}}}; }})()";
        await RunPageScriptAsync(_recordingUploadScript).ConfigureAwait(true);

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState()
        {
            if (IsRecording) started.TrySetResult(true);
        }

        MediaStateChanged += OnState;
        try
        {
            await RunPageScriptAsync(
                    "(async function(){ try { if (window.meetjoeStartRecording) await window.meetjoeStartRecording(); } catch(e) { console.error(e); } })()")
                .ConfigureAwait(true);
            var done = await Task.WhenAny(started.Task, Task.Delay(8_000)).ConfigureAwait(true);
            return done == started.Task && await started.Task.ConfigureAwait(true);
        }
        finally
        {
            MediaStateChanged -= OnState;
        }
    }

    /// <summary>Stop MediaRecorder; page uploads and raises RecordingReady / RecordingFailed.</summary>
    public async Task<Guid?> StopRecordingAsync()
    {
        EnsureWebView();
        if (_webView is null || !_pageReady || !IsRecording)
        {
            IsRecording = false;
            MediaStateChanged?.Invoke();
            _recordingStopTcs?.TrySetResult(null);
            return null;
        }

        _recordingStopTcs = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _webView.ExecuteScriptAsync(
                "(async function(){ try { if (window.meetjoeStopRecording) await window.meetjoeStopRecording(); } catch(e) { console.error(e); } })()");
        }
        catch
        {
            IsRecording = false;
            MediaStateChanged?.Invoke();
            _recordingStopTcs.TrySetResult(null);
            return null;
        }

        // Upload can take a while for long sessions; leave/hang-up still shouldn't hang forever.
        var completed = await Task.WhenAny(_recordingStopTcs.Task, Task.Delay(90_000)).ConfigureAwait(true);
        if (completed != _recordingStopTcs.Task)
        {
            _recordingStopTcs.TrySetResult(null);
            IsRecording = false;
            MediaStateChanged?.Invoke();
            return null;
        }

        return await _recordingStopTcs.Task.ConfigureAwait(true);
    }

    /// <summary>
    /// Minimized call PiP: LiveKit page shows only the active speaker (video or initials).
    /// </summary>
    public Task SetPipModeAsync(bool enabled)
    {
        var flag = enabled ? "true" : "false";
        var script =
            $"(function(){{ try {{ if (window.meetjoeSetPipMode) window.meetjoeSetPipMode({flag}); }} catch(e) {{ console.error(e); }} }})()";
        return RunPageScriptAsync(script);
    }

    /// <summary>Push chrome cam/mic preference into the page and enforce it on the LiveKit room.</summary>
    public async Task SyncPreferredMediaToPageAsync()
    {
        await PushPreferredMediaAsync().ConfigureAwait(true);
        await RunPageScriptAsync(
            "(async function(){ try { if (window.meetjoeApplyPreferredMedia) await window.meetjoeApplyPreferredMedia(); } catch(e) { console.error(e); } })()")
            .ConfigureAwait(true);
    }

    private Task PushPreferredMediaAsync()
    {
        var cam = IsCameraEnabled ? "true" : "false";
        var mic = IsMicEnabled ? "true" : "false";
        var script = $"(function(){{ window.meetjoePreferredMedia={{camera:{cam},mic:{mic}}}; }})()";
        EnsureWebView();
        if (_webView is null) return Task.CompletedTask;
        if (!_pageReady)
        {
            // Keep separate from _pendingScript so PiP / toggle scripts are not overwritten.
            _preferredMediaScript = script;
            return Task.CompletedTask;
        }

        return _webView.ExecuteScriptAsync(script);
    }

    private Task RunPageScriptAsync(string script)
    {
        EnsureWebView();
        if (_webView is null) return Task.CompletedTask;
        if (!_pageReady)
        {
            _pendingScript = script;
            return Task.CompletedTask;
        }

        return _webView.ExecuteScriptAsync(script);
    }

    private async Task ToggleScreenShareCoreAsync()
    {
        const string toggleScript =
            "(async function(){ try { if (window.meetjoeToggleScreenShare) await window.meetjoeToggleScreenShare(); } catch(e) { console.error(e); } })()";

        EnsureWebView();
        if (_webView is null)
        {
            IsScreenShareEnabled = !IsScreenShareEnabled;
            MediaStateChanged?.Invoke();
            return;
        }

        var wantEnabled = !IsScreenShareEnabled;
        IsScreenShareEnabled = wantEnabled;
        MediaStateChanged?.Invoke();

        // Screen share uses getDisplayMedia (system picker) — not AVCapture camera/mic TCC.

        if (!_pageReady)
        {
            _pendingScript = toggleScript;
            return;
        }

        try
        {
            await _webView.ExecuteScriptAsync(toggleScript);
            if (wantEnabled)
            {
                // Screen picker can take a moment; verify in the background without blocking chrome.
                _ = VerifyScreenShareEnabledAsync();
            }
        }
        catch
        {
            _pendingScript = toggleScript;
            if (wantEnabled) RequestPermissionAlert("share");
        }
    }

    private async Task VerifyScreenShareEnabledAsync()
    {
        try
        {
            await Task.Delay(900);
            await PushMediaStateAsync();
            if (!IsScreenShareEnabled) RequestPermissionAlert("share");
        }
        catch
        {
            // Best-effort.
        }
    }

    private async Task ToggleAndSyncAsync(bool mic)
    {
        EnsureWebView();
        if (_webView is null)
        {
            if (mic) IsMicEnabled = !IsMicEnabled;
            else IsCameraEnabled = !IsCameraEnabled;
            MediaStateChanged?.Invoke();
            return;
        }

        // Desired state after this click (VM already flipped chrome optimistically).
        var wantEnabled = mic ? !IsMicEnabled : !IsCameraEnabled;
        if (mic) IsMicEnabled = wantEnabled;
        else IsCameraEnabled = wantEnabled;
        // Keep page prefs in sync so a reconnect / PiP return cannot fight this click.
        _ = PushPreferredMediaAsync();
        MediaStateChanged?.Invoke();

        // Ask macOS TCC first so the system permission dialog can appear (requires .app + Info.plist).
        if (wantEnabled && WebViewPlatformHooks.RequestOsMediaPermissionAsync is { } requestOs)
        {
            var granted = false;
            try { granted = await requestOs(mic ? "mic" : "camera"); }
            catch { granted = true; }

            if (!granted)
            {
                if (mic) IsMicEnabled = false;
                else IsCameraEnabled = false;
                _ = PushPreferredMediaAsync();
                MediaStateChanged?.Invoke();
                RequestPermissionAlert(mic ? "mic" : "camera");
                return;
            }
        }

        var flag = wantEnabled ? "true" : "false";
        var setScript = mic
            ? $"(async function(){{ try {{ if (window.meetjoeSetMicEnabled) await window.meetjoeSetMicEnabled({flag}); }} catch(e) {{ console.error(e); }} }})()"
            : $"(async function(){{ try {{ if (window.meetjoeSetCameraEnabled) await window.meetjoeSetCameraEnabled({flag}); }} catch(e) {{ console.error(e); }} }})()";

        if (!_pageReady)
        {
            _pendingScript = setScript;
            return;
        }

        try
        {
            // Absolute set (not toggle) so preferred-media apply cannot invert the click.
            await _webView.ExecuteScriptAsync(setScript);
            if (wantEnabled)
            {
                await _webView.ExecuteScriptAsync(
                    "(async function(){ try { if (window.meetjoeEnsureAudio) await window.meetjoeEnsureAudio(); } catch(e){} })()");
                _ = VerifyMediaEnabledAsync(mic);
            }
        }
        catch
        {
            _pendingScript = setScript;
            if (wantEnabled) RequestPermissionAlert(mic ? "mic" : "camera");
        }
    }

    private async Task VerifyMediaEnabledAsync(bool mic)
    {
        try
        {
            await Task.Delay(600);
            await PushMediaStateAsync();
            var stillOff = mic ? !IsMicEnabled : !IsCameraEnabled;
            if (stillOff) RequestPermissionAlert(mic ? "mic" : "camera");
        }
        catch
        {
            // Best-effort permission probe.
        }
    }

    private async Task PushMediaStateAsync()
    {
        if (_webView is null) return;
        try
        {
            await _webView.ExecuteScriptAsync(
                "(function(){ try { if (window.meetjoeMediaState) { var s=window.meetjoeMediaState(); var send=window.meetjoeNotifyHost||function(){}; send(s.camera?'camera-on':'camera-off'); send(s.mic?'mic-on':'mic-off'); send(s.share?'share-on':'share-off'); } } catch(e){} })()");
        }
        catch { /* ignore */ }
    }

    private void EnsureWebView()
    {
        if (_webView is not null || _url is null) return;
        try
        {
            _webView = WebViewPlatformHooks.CreateMediaWebView?.Invoke() ?? new LibraryMediaWebView();
        }
        catch (Exception ex)
        {
            // Headless UI tests / missing WebView runtime. Never silent: this hid the
            // .NET 9 macOS WebView crash (no call audio, no TCC prompt) for several releases.
            System.Diagnostics.Trace.TraceError($"MeetJoe media WebView unavailable: {ex}");
            return;
        }

        _webView.MessageReceived += OnMediaMessage;
        _webView.NavigationCompleted += OnNavigationCompleted;
        Children.Clear();
        Children.Add(_webView.View);
        // Navigation happens in ShowAfterOsMediaAsync, after the OS permission step.
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = availableSize;
        if (double.IsInfinity(size.Width) || size.Width <= 0) size = size.WithWidth(640);
        if (double.IsInfinity(size.Height) || size.Height <= 0) size = size.WithHeight(360);
        base.MeasureOverride(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = finalSize;
        if (size.Width <= 0) size = size.WithWidth(Math.Max(MinWidth, 160));
        if (size.Height <= 0) size = size.WithHeight(Math.Max(MinHeight, 120));
        foreach (var child in Children)
            child.Arrange(new Rect(size));
        return size;
    }

    private async void OnNavigationCompleted()
    {
        _pageReady = true;
        // Uncover immediately so getUserMedia is not blocked by Avalonia tiles on macOS.
        Dispatcher.UIThread.Post(() => MediaReady?.Invoke());

        await Task.Delay(500);
        if (_preferredMediaScript is { } preferred)
        {
            _preferredMediaScript = null;
            try { await _webView!.ExecuteScriptAsync(preferred); }
            catch { /* page may still be connecting */ }
        }

        if (_pendingScript is { } pending)
        {
            _pendingScript = null;
            try { await _webView!.ExecuteScriptAsync(pending); }
            catch { /* page may still be connecting */ }
        }

        await PushMediaStateAsync();
        try
        {
            await _webView!.ExecuteScriptAsync(
                "(async function(){ try { if (window.meetjoeEnsureAudio) await window.meetjoeEnsureAudio(); } catch(e){} })()");
        }
        catch { /* ignore */ }
    }

    private void OnMediaMessage(string raw)
    {
        var message = raw.Trim();
        if (string.IsNullOrEmpty(message)) return;

        if (string.Equals(message, "leave", StringComparison.OrdinalIgnoreCase))
        {
            RequestLeave();
            return;
        }

        if (string.Equals(message, "media-ready", StringComparison.OrdinalIgnoreCase))
        {
            // Page waits ~450ms after this before enabling devices — seed prefs first.
            _ = PushPreferredMediaAsync();
            Dispatcher.UIThread.Post(() => MediaReady?.Invoke());
            return;
        }

        if (string.Equals(message, "audio-ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message, "audio-blocked", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => MediaReady?.Invoke());
            return;
        }

        if (string.Equals(message, "permission-mic", StringComparison.OrdinalIgnoreCase))
        {
            IsMicEnabled = false;
            MediaStateChanged?.Invoke();
            RaisePermissionNeeded("mic");
            return;
        }

        if (string.Equals(message, "permission-camera", StringComparison.OrdinalIgnoreCase))
        {
            IsCameraEnabled = false;
            MediaStateChanged?.Invoke();
            RaisePermissionNeeded("camera");
            return;
        }

        if (string.Equals(message, "permission-share", StringComparison.OrdinalIgnoreCase))
        {
            IsScreenShareEnabled = false;
            MediaStateChanged?.Invoke();
            RaisePermissionNeeded("share");
            return;
        }

        if (string.Equals(message, "camera-on", StringComparison.OrdinalIgnoreCase))
        {
            IsCameraEnabled = true;
            MediaStateChanged?.Invoke();
            return;
        }

        if (string.Equals(message, "camera-off", StringComparison.OrdinalIgnoreCase))
        {
            IsCameraEnabled = false;
            MediaStateChanged?.Invoke();
            return;
        }

        if (string.Equals(message, "mic-on", StringComparison.OrdinalIgnoreCase))
        {
            IsMicEnabled = true;
            MediaStateChanged?.Invoke();
            return;
        }

        if (string.Equals(message, "mic-off", StringComparison.OrdinalIgnoreCase))
        {
            IsMicEnabled = false;
            MediaStateChanged?.Invoke();
            return;
        }

        if (string.Equals(message, "share-on", StringComparison.OrdinalIgnoreCase))
        {
            IsScreenShareEnabled = true;
            MediaStateChanged?.Invoke();
            return;
        }

        if (string.Equals(message, "share-off", StringComparison.OrdinalIgnoreCase))
        {
            IsScreenShareEnabled = false;
            MediaStateChanged?.Invoke();
            return;
        }

        if (message.StartsWith("speakers:", StringComparison.OrdinalIgnoreCase))
        {
            ActiveSpeakersChanged?.Invoke(ParseGuidList(message["speakers:".Length..]));
            return;
        }

        if (message.StartsWith("mutes:", StringComparison.OrdinalIgnoreCase))
        {
            var muted = ParseGuidList(message["mutes:".Length..]);
            Dispatcher.UIThread.Post(() => MutedParticipantsChanged?.Invoke(muted));
            return;
        }

        if (message.StartsWith("session-start:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = message["session-start:".Length..].Trim();
            if (long.TryParse(payload, out var ms) && ms > 0)
                Dispatcher.UIThread.Post(() => SessionStarted?.Invoke(ms));
            return;
        }

        if (string.Equals(message, "session-warn", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => SessionWarning?.Invoke());
            return;
        }

        if (string.Equals(message, "session-expired", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => SessionExpired?.Invoke());
            return;
        }

        if (string.Equals(message, "recording-on", StringComparison.OrdinalIgnoreCase))
        {
            IsRecording = true;
            Dispatcher.UIThread.Post(() => MediaStateChanged?.Invoke());
            return;
        }

        if (string.Equals(message, "recording-off", StringComparison.OrdinalIgnoreCase))
        {
            // UI-only: chrome can drop the REC indicator while upload finishes.
            // Do NOT complete _recordingStopTcs here — that must wait for
            // recording-ready / recording-failed so FinalizeRecordingAsync gets the file id.
            IsRecording = false;
            Dispatcher.UIThread.Post(() =>
            {
                MediaStateChanged?.Invoke();
                RecordingTimerChanged?.Invoke(null);
            });
            return;
        }

        if (string.Equals(message, "recording-timer-off", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => RecordingTimerChanged?.Invoke(null));
            return;
        }

        if (message.StartsWith("recording-timer:", StringComparison.OrdinalIgnoreCase))
        {
            var timerPayload = message["recording-timer:".Length..].Trim();
            long? startedAt = long.TryParse(timerPayload, out var ms) ? ms : null;
            Dispatcher.UIThread.Post(() => RecordingTimerChanged?.Invoke(startedAt));
            return;
        }

        if (string.Equals(message, "room-recording-off", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => RoomRecordingChanged?.Invoke(false, null, null));
            return;
        }

        if (message.StartsWith("room-recording-on:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = message["room-recording-on:".Length..];
            long? startedAt = null;
            string? by = null;
            var pipe = payload.IndexOf('|');
            if (pipe >= 0)
            {
                if (long.TryParse(payload[..pipe], out var ms))
                    startedAt = ms;
                by = Uri.UnescapeDataString(payload[(pipe + 1)..].Trim());
            }
            else
            {
                by = Uri.UnescapeDataString(payload.Trim());
            }

            Dispatcher.UIThread.Post(() => RoomRecordingChanged?.Invoke(true, by, startedAt));
            return;
        }

        if (message.StartsWith("recording-ready:", StringComparison.OrdinalIgnoreCase))
        {
            IsRecording = false;
            var idText = message["recording-ready:".Length..].Trim();
            Guid? fileId = Guid.TryParse(idText, out var parsed) ? parsed : null;
            Dispatcher.UIThread.Post(() =>
            {
                MediaStateChanged?.Invoke();
                RecordingTimerChanged?.Invoke(null);
                if (fileId is { } id) RecordingReady?.Invoke(id);
                _recordingStopTcs?.TrySetResult(fileId);
            });
            return;
        }

        if (message.StartsWith("recording-failed:", StringComparison.OrdinalIgnoreCase))
        {
            IsRecording = false;
            var detail = message["recording-failed:".Length..].Trim();
            Dispatcher.UIThread.Post(() =>
            {
                MediaStateChanged?.Invoke();
                RecordingTimerChanged?.Invoke(null);
                RecordingFailed?.Invoke(string.IsNullOrWhiteSpace(detail) ? "Recording failed." : detail);
                _recordingStopTcs?.TrySetResult(null);
            });
        }
    }

    private static List<Guid> ParseGuidList(string payload)
    {
        var ids = new List<Guid>();
        if (string.IsNullOrWhiteSpace(payload)) return ids;
        foreach (var part in payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>Raise even on repeat failures so toggling camera/mic always surfaces a warning.</summary>
    public void RequestPermissionAlert(string kind) =>
        Dispatcher.UIThread.Post(() => PermissionNeeded?.Invoke(kind));

    private void RaisePermissionNeeded(string kind)
    {
        // First automatic failure per session is enough to prompt; toggles call RequestPermissionAlert.
        if (!_permissionAlerted.Add(kind)) return;
        RequestPermissionAlert(kind);
    }

    private void RequestLeave() => Dispatcher.UIThread.Post(() => _onLeave?.Invoke());
}
