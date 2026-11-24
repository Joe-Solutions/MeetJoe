#if OSX
using System.Reflection;
using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using WebKit;
using AvaloniaWebViewControl = AvaloniaWebView.WebView;

namespace MeetJoe.Desktop;

/// <summary>
/// Avalonia.WebView's WKUIDelegate does not implement macOS 12+ media-capture permission.
/// Without Grant, LiveKit getUserMedia (camera/mic) fails silently in the in-app WebView.
/// </summary>
internal static class MacOsWebViewMediaAccess
{
    public static void Attach(AvaloniaWebViewControl webView)
    {
        try
        {
            webView.WebViewCreated -= OnCreated;
            webView.WebViewCreated += OnCreated;
            if (webView.PlatformWebView?.IsInitialized == true)
                TryInstall(webView);
        }
        catch
        {
            // Best-effort; media may still work on older OS builds.
        }
    }

    private static void OnCreated(object? sender, WebViewCore.Events.WebViewCreatedEventArgs e)
    {
        if (sender is AvaloniaWebViewControl webView)
            TryInstall(webView);
    }

    private static void TryInstall(AvaloniaWebViewControl webView)
    {
        var platform = webView.PlatformWebView;
        if (platform is null) return;

        // Avalonia.WebView.MacCatalyst.Core.MacCatalystWebViewCore.WebView
        var wkProp = platform.GetType().GetProperty("WebView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (wkProp?.GetValue(platform) is not WKWebView wk) return;

        try
        {
            // Allow remote LiveKit audio/video without a second user gesture.
            wk.Configuration.MediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypes.None;
        }
        catch
        {
            // Older WebKit builds may not expose this setter after construction.
        }

        try
        {
            // Ensure getUserMedia is available inside the embedded WKWebView.
            wk.Configuration.Preferences?.SetValueForKey(NSObject.FromObject(true), new NSString("mediaDevicesEnabled"));
        }
        catch
        {
            // Preference key is undocumented; ignore if unavailable.
        }

        if (wk.UIDelegate is MediaCaptureUIDelegate) return;
        wk.UIDelegate = new MediaCaptureUIDelegate(wk.UIDelegate as WKUIDelegate);
    }

    private sealed class MediaCaptureUIDelegate : WKUIDelegate
    {
        private readonly WKUIDelegate? _inner;

        public MediaCaptureUIDelegate(WKUIDelegate? inner) => _inner = inner;

        public override void RunJavaScriptAlertPanel(WKWebView webView, string message, WKFrameInfo frame, Action completionHandler)
        {
            if (_inner is not null) _inner.RunJavaScriptAlertPanel(webView, message, frame, completionHandler);
            else completionHandler();
        }

        public override void RunJavaScriptConfirmPanel(WKWebView webView, string message, WKFrameInfo frame, Action<bool> completionHandler)
        {
            if (_inner is not null) _inner.RunJavaScriptConfirmPanel(webView, message, frame, completionHandler);
            else completionHandler(false);
        }

        public override void RunJavaScriptTextInputPanel(
            WKWebView webView, string prompt, string? defaultText, WKFrameInfo frame, Action<string> completionHandler)
        {
            if (_inner is not null) _inner.RunJavaScriptTextInputPanel(webView, prompt, defaultText, frame, completionHandler);
            else completionHandler(defaultText ?? string.Empty);
        }

        // macOS 12+ / WKWebView: required for getUserMedia. Not present on the shipped Xamarin.Mac
        // WKUIDelegate API surface, so Export + raw block invoke.
        [Export("webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:")]
        public void RequestMediaCapturePermission(
            WKWebView webView,
            WKSecurityOrigin origin,
            WKFrameInfo frame,
            nint captureType,
            IntPtr decisionHandlerBlock)
        {
            // WKPermissionDecisionGrant = 1
            InvokePermissionDecision(decisionHandlerBlock, 1);
        }

        private static void InvokePermissionDecision(IntPtr block, nint decision)
        {
            if (block == IntPtr.Zero) return;
            // Apple Block_layout: isa, flags, reserved, invoke, descriptor — invoke at offset 16 on 64-bit.
            var invokePtr = Marshal.ReadIntPtr(block, IntPtr.Size == 8 ? 16 : 12);
            if (invokePtr == IntPtr.Zero) return;
            var invoke = Marshal.GetDelegateForFunctionPointer<PermissionDecisionInvoke>(invokePtr);
            invoke(block, decision);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PermissionDecisionInvoke(IntPtr block, nint decision);
    }
}
#endif
