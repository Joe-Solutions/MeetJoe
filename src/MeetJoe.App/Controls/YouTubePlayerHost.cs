using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MeetJoe.App.Controls;

/// <summary>
/// In-app WebView for YouTube watch/browse URLs. Reuses the same native WebView hooks as LiveKit
/// (WKWebView on macOS, WebView2 on Windows) without requesting mic/camera.
/// </summary>
public sealed class YouTubePlayerHost : Panel
{
    private IMediaWebView? _webView;
    private Uri? _url;

    public YouTubePlayerHost()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        ClipToBounds = false;
        Background = Brushes.Transparent;
        MinWidth = 160;
        MinHeight = 120;
    }

    public bool IsAvailable => _webView is not null;
    public Uri? CurrentUrl => _url;

    public void Show(Uri url)
    {
        _url = url;
        EnsureWebView();
        if (_webView is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_webView is null || _url != url) return;
            _webView.Navigate(url);
        }, DispatcherPriority.Loaded);

        InvalidateMeasure();
        InvalidateArrange();
    }

    public Task PauseAsync() =>
        ExecuteSafeAsync(
            "(function(){ try { var v=document.querySelector('video'); if(v){ v.pause(); } " +
            "var yt=document.getElementById('movie_player'); if(yt&&yt.pauseVideo) yt.pauseVideo(); } catch(e){} })()");

    public Task PlayAsync() =>
        ExecuteSafeAsync(
            "(function(){ try { var v=document.querySelector('video'); if(v){ v.play(); } " +
            "var yt=document.getElementById('movie_player'); if(yt&&yt.playVideo) yt.playVideo(); } catch(e){} })()");

    public async Task ShutdownAsync()
    {
        _url = null;
        var web = _webView;
        _webView = null;
        if (web is null)
        {
            Children.Clear();
            return;
        }

        try { await web.ShutdownAsync(); }
        catch { /* ignore */ }

        Children.Clear();
    }

    private async Task ExecuteSafeAsync(string script)
    {
        if (_webView is null) return;
        try { await _webView.ExecuteScriptAsync(script); }
        catch { /* page may not be ready */ }
    }

    private void EnsureWebView()
    {
        if (_webView is not null) return;

        try
        {
            _webView = WebViewPlatformHooks.CreateMediaWebView?.Invoke() ?? new LibraryMediaWebView();
        }
        catch
        {
            _webView = null;
            return;
        }

        Children.Clear();
        Children.Add(_webView.View);
    }
}
