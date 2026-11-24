using Avalonia.Controls;

namespace MeetJoe.App.Controls;

/// <summary>
/// The native browser surface that runs the LiveKit meeting page for <see cref="InAppMediaHost"/>.
/// macOS uses a direct WKWebView host; Windows uses WebView.Avalonia's WebView2 backend.
/// </summary>
public interface IMediaWebView
{
    /// <summary>The Avalonia control to place in the visual tree.</summary>
    Control View { get; }

    void Navigate(Uri url);

    /// <summary>Runs script in the page. Results are not returned; callers only need ordering.</summary>
    Task ExecuteScriptAsync(string script);

    /// <summary>Messages the page posts via <c>window.meetjoeNotifyHost</c>.</summary>
    event Action<string>? MessageReceived;

    event Action? NavigationCompleted;

    /// <summary>Stop the page and release the native view (and with it, camera/mic devices).</summary>
    Task ShutdownAsync();
}
