#if OSX
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MeetJoe.App.Controls;

namespace MeetJoe.Desktop;

/// <summary>
/// WKWebView hosted directly through libobjc, replacing WebView.Avalonia on macOS: that
/// package's bundled Xamarin.Mac registrar throws on .NET 9+, so its WebView never loaded.
/// One runtime-registered ObjC class (<c>MeetJoeWKBridge</c>) acts as script message handler
/// ("webview", matching the meeting page's notifyHost), navigation delegate and UI delegate.
/// </summary>
internal sealed class MacMediaWebView : NativeControlHost, IMediaWebView
{
    private const string MessageHandlerName = "webview";
    private const nint PermissionGrant = 1; // WKPermissionDecisionGrant

    private static readonly Dictionary<IntPtr, WeakReference<MacMediaWebView>> Bridges = new();

    private IntPtr _webView;
    private IntPtr _bridge;

    public MacMediaWebView()
    {
        MinWidth = 160;
        MinHeight = 120;
        // Created eagerly so Navigate works before the control is attached.
        CreateWebView();
    }

    public Control View => this;

    public event Action<string>? MessageReceived;
    public event Action? NavigationCompleted;

    public void Navigate(Uri url)
    {
        if (_webView == IntPtr.Zero) return;
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var str = ObjC.NewNSString(url.AbsoluteUri);
            var nsUrl = ObjC.Send(ObjC.Class("NSURL"), "URLWithString:", str);
            ObjC.Release(str);
            if (nsUrl == IntPtr.Zero) return;
            var request = ObjC.Send(ObjC.Class("NSURLRequest"), "requestWithURL:", nsUrl);
            ObjC.Send(_webView, "loadRequest:", request);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    public Task ExecuteScriptAsync(string script)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(() => ExecuteScriptAsync(script));
        if (_webView == IntPtr.Zero) return Task.CompletedTask;

        var js = ObjC.NewNSString(script);
        // nil completion handler: callers only need the script queued in order.
        ObjC.Send(_webView, "evaluateJavaScript:completionHandler:", js, IntPtr.Zero);
        ObjC.Release(js);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        var web = _webView;
        if (web == IntPtr.Zero) return;

        ObjC.Send(web, "stopLoading");
        // Navigating away releases LiveKit's capture tracks before the view goes.
        Navigate(new Uri("about:blank"));
        await Task.Delay(150);

        var controller = ObjC.Send(ObjC.Send(web, "configuration"), "userContentController");
        var name = ObjC.NewNSString(MessageHandlerName);
        ObjC.Send(controller, "removeScriptMessageHandlerForName:", name);
        ObjC.Release(name);
        ObjC.Send(web, "setNavigationDelegate:", IntPtr.Zero);
        ObjC.Send(web, "setUIDelegate:", IntPtr.Zero);

        lock (Bridges) Bridges.Remove(_bridge);
        ObjC.Release(_bridge);
        _bridge = IntPtr.Zero;
        // The superview still retains the view until Avalonia detaches it.
        ObjC.Release(web);
        _webView = IntPtr.Zero;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent) =>
        _webView != IntPtr.Zero
            ? new PlatformHandle(_webView, "NSView")
            : base.CreateNativeControlCore(parent);

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Owned by ShutdownAsync; the host may re-attach the same view (Minimize / stage).
        if (control.Handle != _webView)
            base.DestroyNativeControlCore(control);
    }

    private void CreateWebView()
    {
        BridgeClass.EnsureRegistered();

        var config = ObjC.Send(ObjC.Send(ObjC.Class("WKWebViewConfiguration"), "alloc"), "init");
        // Remote LiveKit audio/video must play without an extra user gesture.
        ObjC.SendNUInt(config, "setMediaTypesRequiringUserActionForPlayback:", 0);

        _bridge = ObjC.Send(ObjC.Send(BridgeClass.Handle, "alloc"), "init");
        lock (Bridges) Bridges[_bridge] = new WeakReference<MacMediaWebView>(this);

        var controller = ObjC.Send(config, "userContentController");
        var name = ObjC.NewNSString(MessageHandlerName);
        ObjC.Send(controller, "addScriptMessageHandler:name:", _bridge, name);
        ObjC.Release(name);

        var alloc = ObjC.Send(ObjC.Class("WKWebView"), "alloc");
        _webView = ObjC.SendInitWithFrame(alloc, "initWithFrame:configuration:", new CGRect(0, 0, 640, 360), config);
        ObjC.Release(config);

        ObjC.Send(_webView, "setNavigationDelegate:", _bridge);
        ObjC.Send(_webView, "setUIDelegate:", _bridge);
    }

    private static MacMediaWebView? FromBridge(IntPtr bridge)
    {
        lock (Bridges)
            return Bridges.TryGetValue(bridge, out var weak) && weak.TryGetTarget(out var view) ? view : null;
    }

    /// <summary>The ObjC delegate class, registered once with the runtime.</summary>
    private static class BridgeClass
    {
        // Rooted forever: ObjC holds raw function pointers to these.
        private static readonly ScriptMessageImp OnScriptMessage = DidReceiveScriptMessage;
        private static readonly NavigationImp OnFinishNavigation = DidFinishNavigation;
        private static readonly MediaPermissionImp OnMediaPermission = RequestMediaCapturePermission;

        public static IntPtr Handle { get; private set; }

        public static void EnsureRegistered()
        {
            if (Handle != IntPtr.Zero) return;
            NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");

            var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "MeetJoeWKBridge", 0);
            if (cls == IntPtr.Zero)
            {
                // Already registered by an earlier load in this process.
                Handle = ObjC.Class("MeetJoeWKBridge");
                return;
            }

            ObjC.class_addMethod(cls, ObjC.Sel("userContentController:didReceiveScriptMessage:"),
                Marshal.GetFunctionPointerForDelegate(OnScriptMessage), "v@:@@");
            ObjC.class_addMethod(cls, ObjC.Sel("webView:didFinishNavigation:"),
                Marshal.GetFunctionPointerForDelegate(OnFinishNavigation), "v@:@@");
            // macOS 12+: without this WebKit prompts per origin (or denies) for getUserMedia.
            ObjC.class_addMethod(cls, ObjC.Sel("webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:"),
                Marshal.GetFunctionPointerForDelegate(OnMediaPermission), "v@:@@@q@?");

            foreach (var protocol in new[] { "WKScriptMessageHandler", "WKNavigationDelegate", "WKUIDelegate" })
            {
                var p = ObjC.objc_getProtocol(protocol);
                if (p != IntPtr.Zero) ObjC.class_addProtocol(cls, p);
            }

            ObjC.objc_registerClassPair(cls);
            Handle = cls;
        }

        private static void DidReceiveScriptMessage(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message)
        {
            try
            {
                var view = FromBridge(self);
                if (view is null) return;
                var body = ObjC.Send(message, "body");
                var text = ObjC.ToManagedString(body);
                if (text is not null)
                    view.MessageReceived?.Invoke(text);
            }
            catch
            {
                // Never let an exception unwind into WebKit.
            }
        }

        private static void DidFinishNavigation(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
        {
            try { FromBridge(self)?.NavigationCompleted?.Invoke(); }
            catch { /* see above */ }
        }

        private static void RequestMediaCapturePermission(
            IntPtr self, IntPtr sel, IntPtr webView, IntPtr origin, IntPtr frame, nint type, IntPtr decisionHandler)
        {
            // TCC already gated the app (MacOsMediaPermissions); grant the meeting page like Teams.
            try { ObjC.InvokeBlock(decisionHandler, PermissionGrant); }
            catch { /* see above */ }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ScriptMessageImp(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NavigationImp(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MediaPermissionImp(
            IntPtr self, IntPtr sel, IntPtr webView, IntPtr origin, IntPtr frame, nint type, IntPtr decisionHandler);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CGRect(double X, double Y, double Width, double Height);

/// <summary>Minimal libobjc interop for the WKWebView host.</summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    public static IntPtr Class(string name) => objc_getClass(name);
    public static IntPtr Sel(string name) => sel_registerName(name);

    public static IntPtr Send(IntPtr receiver, string selector) => Msg(receiver, Sel(selector));
    public static IntPtr Send(IntPtr receiver, string selector, IntPtr a) => Msg(receiver, Sel(selector), a);
    public static IntPtr Send(IntPtr receiver, string selector, IntPtr a, IntPtr b) => Msg(receiver, Sel(selector), a, b);
    public static void SendNUInt(IntPtr receiver, string selector, nuint value) => MsgNUInt(receiver, Sel(selector), value);
    public static IntPtr SendInitWithFrame(IntPtr receiver, string selector, CGRect frame, IntPtr config) =>
        MsgRect(receiver, Sel(selector), frame, config);

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) Msg(obj, Sel("release"));
    }

    /// <summary>+1 retained NSString; the caller releases it.</summary>
    public static IntPtr NewNSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Msg(Msg(Class("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public static string? ToManagedString(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return null;
        var isString = MsgBool(obj, Sel("isKindOfClass:"), Class("NSString")) != 0;
        var str = isString ? obj : Msg(obj, Sel("description"));
        return Marshal.PtrToStringUTF8(Msg(str, Sel("UTF8String")));
    }

    /// <summary>Calls a <c>void (^)(NSInteger)</c> block (Block_layout invoke slot at offset 16).</summary>
    public static void InvokeBlock(IntPtr block, nint arg)
    {
        if (block == IntPtr.Zero) return;
        var invoke = Marshal.ReadIntPtr(block, 16);
        Marshal.GetDelegateForFunctionPointer<BlockInvokeNInt>(invoke)(block, arg);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockInvokeNInt(IntPtr block, nint arg);

    [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] public static extern IntPtr objc_getProtocol(string name);
    [DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] public static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Lib)] public static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern byte MsgBool(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern void MsgNUInt(IntPtr r, IntPtr s, nuint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgRect(IntPtr r, IntPtr s, CGRect frame, IntPtr a);
}
#endif
