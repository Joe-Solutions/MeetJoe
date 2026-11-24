using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaWebView;
using WebViewCore.Events;

namespace MeetJoe.App.Controls;

/// <summary>
/// <see cref="IMediaWebView"/> over WebView.Avalonia. Used on Windows (WebView2). Not used on
/// macOS: the package's bundled Xamarin.Mac registrar throws on .NET 9+.
/// </summary>
public sealed class LibraryMediaWebView : IMediaWebView
{
    private readonly WebView _webView;

    public LibraryMediaWebView()
    {
        _webView = new WebView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinWidth = 160,
            MinHeight = 120,
        };
        _webView.WebMessageReceived += OnMessage;
        _webView.NavigationStarting += OnNavigating;
        _webView.NavigationCompleted += OnNavigationCompleted;
        WebViewPlatformHooks.OnWebViewCreated?.Invoke(_webView);
    }

    public Control View => _webView;

    public event Action<string>? MessageReceived;
    public event Action? NavigationCompleted;

    public void Navigate(Uri url) => _webView.Url = url;

    public Task ExecuteScriptAsync(string script) => _webView.ExecuteScriptAsync(script);

    public async Task ShutdownAsync()
    {
        _webView.WebMessageReceived -= OnMessage;
        _webView.NavigationStarting -= OnNavigating;
        _webView.NavigationCompleted -= OnNavigationCompleted;

        try { _webView.Stop(); }
        catch { /* ignore */ }

        try { _webView.Url = new Uri("about:blank"); }
        catch { /* ignore */ }

        // Let the engine release camera/mic devices before native dispose.
        await Task.Delay(150);

        try
        {
            if (_webView.PlatformWebView is IDisposable platform)
                platform.Dispose();
        }
        catch { /* ignore */ }
    }

    private void OnMessage(object? sender, WebViewMessageReceivedEventArgs e)
    {
        if (e.Message is { } message)
            MessageReceived?.Invoke(message);
    }

    private void OnNavigating(object? sender, WebViewUrlLoadingEventArg e)
    {
        // Fallback leave signal when postMessage is unavailable (page sets #meetjoe-leave=…).
        if (e.Url?.Fragment.Contains("meetjoe-leave", StringComparison.Ordinal) == true)
        {
            e.Cancel = true;
            MessageReceived?.Invoke("leave");
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e) =>
        NavigationCompleted?.Invoke();
}
