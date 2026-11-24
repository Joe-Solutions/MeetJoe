using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaWebView;
using WebViewCore.Events;

namespace MeetJoe.App.Controls;

/// <summary>
/// Lazily hosts the LiveKit WebView. Constructed only when media is open so section
/// pages (Calls, Calendar) stay usable when the WebView runtime is unavailable.
/// </summary>
public sealed class InAppMediaHost : Panel
{
    private WebView? _webView;
    private Uri? _url;
    private Action? _onLeave;
    private bool _pageReady;
    private string? _pendingScript;

    public bool IsCameraEnabled { get; private set; } = true;
    public bool IsMicEnabled { get; private set; } = true;
    public bool IsAvailable => _webView is not null;

    public event Action? MediaStateChanged;
    public event Action? MediaReady;
    public event Action<IReadOnlyList<Guid>>? ActiveSpeakersChanged;
    public event Action<IReadOnlyList<Guid>>? MutedParticipantsChanged;

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

        if (changed || _webView.Url != url)
        {
            _pageReady = false;
            _webView.Url = url;
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    public void Hide() => _ = ShutdownAsync();

    /// <summary>
    /// Stop LiveKit capture and dispose the native WebView before the window tears it down.
    /// Skipping this on macOS often segfaults WKWebView (crash dialog on quit).
    /// </summary>
    public async Task ShutdownAsync()
    {
        _url = null;
        _pageReady = false;
        _pendingScript = null;
        _permissionAlerted.Clear();

        var web = _webView;
        _webView = null;
        if (web is null)
        {
            Children.Clear();
            return;
        }

        web.WebMessageReceived -= OnMediaMessage;
        web.NavigationStarting -= OnMediaNavigating;
        web.NavigationCompleted -= OnNavigationCompleted;

        try
        {
            await web.ExecuteScriptAsync(
                "(async function(){ try { if (window.meetjoeShutdown) await window.meetjoeShutdown(); } catch(e){} })()");
        }
        catch { /* page may already be gone */ }

        try { web.Stop(); }
        catch { /* ignore */ }

        try { web.Url = new Uri("about:blank"); }
        catch { /* ignore */ }

        // Let WebKit release camera/mic devices before native dispose.
        await Task.Delay(150);

        try
        {
            if (web.PlatformWebView is IDisposable platform)
                platform.Dispose();
        }
        catch { /* ignore */ }

        Children.Clear();
    }

    public Task ToggleCameraAsync() => ToggleAndSyncAsync(
        "(async function(){ try { if (window.meetjoeToggleCamera) await window.meetjoeToggleCamera(); } catch(e) { console.error(e); } })()",
        mic: false);

    public Task ToggleMicAsync() => ToggleAndSyncAsync(
        "(async function(){ try { if (window.meetjoeToggleMic) await window.meetjoeToggleMic(); } catch(e) { console.error(e); } })()",
        mic: true);

    private async Task ToggleAndSyncAsync(string toggleScript, bool mic)
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
                MediaStateChanged?.Invoke();
                RequestPermissionAlert(mic ? "mic" : "camera");
                return;
            }
        }

        if (!_pageReady)
        {
            _pendingScript = toggleScript;
            return;
        }

        try
        {
            await _webView.ExecuteScriptAsync(toggleScript);
            // Button click is a user gesture — unlock WKWebView remote audio autoplay.
            await _webView.ExecuteScriptAsync(
                "(async function(){ try { if (window.meetjoeEnsureAudio) await window.meetjoeEnsureAudio(); } catch(e){} })()");
            await Task.Delay(400);
            await PushMediaStateAsync();

            // If the user tried to turn media on but LiveKit/OS kept it off, warn about permissions.
            var stillOff = mic ? !IsMicEnabled : !IsCameraEnabled;
            if (wantEnabled && stillOff)
                RequestPermissionAlert(mic ? "mic" : "camera");
        }
        catch
        {
            _pendingScript = toggleScript;
            if (wantEnabled) RequestPermissionAlert(mic ? "mic" : "camera");
        }
    }

    private async Task PushMediaStateAsync()
    {
        if (_webView is null) return;
        try
        {
            await _webView.ExecuteScriptAsync(
                "(function(){ try { if (window.meetjoeMediaState) { var s=window.meetjoeMediaState(); var send=window.meetjoeNotifyHost||function(){}; send(s.camera?'camera-on':'camera-off'); send(s.mic?'mic-on':'mic-off'); } } catch(e){} })()");
        }
        catch { /* ignore */ }
    }

    private void EnsureWebView()
    {
        if (_webView is not null || _url is null) return;
        try
        {
            _webView = new WebView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinWidth = 160,
                MinHeight = 120,
            };
        }
        catch (Exception)
        {
            // Headless UI tests / missing WebView runtime.
            return;
        }

        _webView.WebMessageReceived += OnMediaMessage;
        _webView.NavigationStarting += OnMediaNavigating;
        _webView.NavigationCompleted += OnNavigationCompleted;
        WebViewPlatformHooks.OnWebViewCreated?.Invoke(_webView);
        Children.Clear();
        Children.Add(_webView);
        _webView.Url = _url;
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

    private async void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e)
    {
        _pageReady = true;
        // Uncover immediately so getUserMedia is not blocked by Avalonia tiles on macOS.
        Dispatcher.UIThread.Post(() => MediaReady?.Invoke());

        await Task.Delay(500);
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

    private void OnMediaMessage(object? sender, WebViewMessageReceivedEventArgs e)
    {
        var message = e.Message?.Trim();
        if (string.IsNullOrEmpty(message)) return;

        if (string.Equals(message, "leave", StringComparison.OrdinalIgnoreCase))
        {
            RequestLeave();
            return;
        }

        if (string.Equals(message, "media-ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message, "audio-ok", StringComparison.OrdinalIgnoreCase)
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

        if (message.StartsWith("speakers:", StringComparison.OrdinalIgnoreCase))
        {
            ActiveSpeakersChanged?.Invoke(ParseGuidList(message["speakers:".Length..]));
            return;
        }

        if (message.StartsWith("mutes:", StringComparison.OrdinalIgnoreCase))
        {
            var muted = ParseGuidList(message["mutes:".Length..]);
            Dispatcher.UIThread.Post(() => MutedParticipantsChanged?.Invoke(muted));
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

    private void OnMediaNavigating(object? sender, WebViewUrlLoadingEventArg e)
    {
        if (e.Url?.Fragment.Contains("meetjoe-leave", StringComparison.Ordinal) == true)
        {
            e.Cancel = true;
            RequestLeave();
        }
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
