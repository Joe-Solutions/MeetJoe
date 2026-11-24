using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using AvaloniaWebViewControl = AvaloniaWebView.WebView;

namespace MeetJoe.Desktop;

/// <summary>
/// Avalonia.WebView's WebView2 core never handles PermissionRequested, so LiveKit
/// getUserMedia depends on WebView2's own prompt (often hidden behind the host window).
/// Grant mic/camera for the in-app meeting page, Teams-style; Windows Privacy settings
/// still have the final say at the OS level.
/// </summary>
internal static class WindowsWebViewMediaAccess
{
    private static readonly ConditionalWeakTable<CoreWebView2, object> Hooked = new();

    public static void Attach(AvaloniaWebViewControl webView)
    {
        try
        {
            webView.WebViewCreated -= OnCreated;
            webView.WebViewCreated += OnCreated;
            // Core may finish initializing after WebViewCreated; LiveKit only asks for
            // media after the page navigates, so this is early enough.
            webView.NavigationStarting -= OnNavigating;
            webView.NavigationStarting += OnNavigating;
            TryInstall(webView);
        }
        catch
        {
            // Best-effort; WebView2 falls back to its default prompt.
        }
    }

    private static void OnCreated(object? sender, WebViewCore.Events.WebViewCreatedEventArgs e)
    {
        if (sender is AvaloniaWebViewControl webView)
            TryInstall(webView);
    }

    private static void OnNavigating(object? sender, WebViewCore.Events.WebViewUrlLoadingEventArg e)
    {
        if (sender is AvaloniaWebViewControl webView)
            TryInstall(webView);
    }

    private static void TryInstall(AvaloniaWebViewControl webView)
    {
        try
        {
            var core = FindCoreWebView2(webView.PlatformWebView);
            if (core is null || Hooked.TryGetValue(core, out _)) return;
            Hooked.Add(core, new object());
            core.PermissionRequested += OnPermissionRequested;
        }
        catch
        {
            // Core not ready yet; the next navigation retries.
        }
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera)
            e.State = CoreWebView2PermissionState.Allow;
    }

    // Avalonia.WebView.Windows.Core.WebView2Core keeps the controller in a private member.
    private static CoreWebView2? FindCoreWebView2(object? platform)
    {
        if (platform is null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = platform.GetType();

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            if (prop.PropertyType == typeof(CoreWebView2))
                return prop.GetValue(platform) as CoreWebView2;
            if (prop.PropertyType == typeof(CoreWebView2Controller))
                return (prop.GetValue(platform) as CoreWebView2Controller)?.CoreWebView2;
        }

        foreach (var field in type.GetFields(flags))
        {
            if (field.FieldType == typeof(CoreWebView2))
                return field.GetValue(platform) as CoreWebView2;
            if (field.FieldType == typeof(CoreWebView2Controller))
                return (field.GetValue(platform) as CoreWebView2Controller)?.CoreWebView2;
        }

        return null;
    }
}
