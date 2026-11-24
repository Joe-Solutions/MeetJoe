using AvaloniaWebView;

namespace MeetJoe.App.Controls;

/// <summary>
/// Desktop hosts register platform-specific WebView pieces (macOS WKWebView host, TCC, WebView2 grant).
/// </summary>
public static class WebViewPlatformHooks
{
    /// <summary>
    /// Creates the media WebView. Null falls back to <see cref="LibraryMediaWebView"/> (WebView.Avalonia).
    /// </summary>
    public static Func<IMediaWebView>? CreateMediaWebView { get; set; }

    /// <summary>Called for each WebView.Avalonia instance (Windows WebView2 permission grant).</summary>
    public static Action<WebView>? OnWebViewCreated { get; set; }

    /// <summary>
    /// Ask the OS for camera/mic access ("camera" or "mic"). Return false when denied.
    /// </summary>
    public static Func<string, Task<bool>>? RequestOsMediaPermissionAsync { get; set; }
}
