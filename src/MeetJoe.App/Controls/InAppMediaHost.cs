using Avalonia.Controls;
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

    public void SetLeaveHandler(Action? onLeave) => _onLeave = onLeave;

    public void Show(Uri url)
    {
        _url = url;
        EnsureWebView();
        if (_webView is not null && _webView.Url != url)
            _webView.Url = url;
    }

    public void Hide()
    {
        _url = null;
        if (_webView is null) return;
        _webView.WebMessageReceived -= OnMediaMessage;
        _webView.NavigationStarting -= OnMediaNavigating;
        Children.Clear();
        _webView = null;
    }

    private void EnsureWebView()
    {
        if (_webView is not null || _url is null) return;
        try
        {
            _webView = new WebView();
        }
        catch (Exception)
        {
            // Headless UI tests / missing WebView runtime.
            return;
        }

        _webView.WebMessageReceived += OnMediaMessage;
        _webView.NavigationStarting += OnMediaNavigating;
        Children.Clear();
        Children.Add(_webView);
        _webView.Url = _url;
    }

    private void OnMediaMessage(object? sender, WebViewMessageReceivedEventArgs e)
    {
        if (string.Equals(e.Message, "leave", StringComparison.OrdinalIgnoreCase))
            RequestLeave();
    }

    private void OnMediaNavigating(object? sender, WebViewUrlLoadingEventArg e)
    {
        if (e.Url?.Fragment.Contains("meetjoe-leave", StringComparison.Ordinal) == true)
        {
            e.Cancel = true;
            RequestLeave();
        }
    }

    private void RequestLeave() => Dispatcher.UIThread.Post(() => _onLeave?.Invoke());
}
