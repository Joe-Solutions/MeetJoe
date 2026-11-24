using AvaloniaWebView;

namespace MeetJoe.App.Controls;

/// <summary>
/// Desktop hosts register platform-specific WebView fixes (e.g. macOS camera/mic grant).
/// </summary>
public static class WebViewPlatformHooks
{
    public static Action<WebView>? OnWebViewCreated { get; set; }

    /// <summary>
    /// Ask the OS for camera/mic access ("camera" or "mic"). Return false when denied.
    /// </summary>
    public static Func<string, Task<bool>>? RequestOsMediaPermissionAsync { get; set; }
}
