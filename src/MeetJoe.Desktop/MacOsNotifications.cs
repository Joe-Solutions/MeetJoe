#if OSX
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MeetJoe.Desktop;

/// <summary>
/// Posts Notification Center banners as MeetJoe (UNUserNotificationCenter), not via osascript.
/// Click / Show brings MeetJoe forward and hands off meetjoe:// deep links to the running UI.
/// </summary>
internal static class MacOsNotifications
{
    private const string CategoryId = "MEETJOE_OPEN";
    private const string ShowActionId = "MEETJOE_SHOW";

    // Banner | Sound | List — show while MeetJoe is focused too.
    private const nuint PresentBannerSoundList = 16 | 2 | 8;
    private const nuint AuthAlertSoundBadge = 4 | 2 | 1;
    private const nuint ActionForeground = 1u << 2;

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, string> PendingUris = new();

    private static IntPtr _userNotifications;
    private static IntPtr _delegate;
    private static bool _ready;

    private static readonly DidReceiveDelegate DidReceive = OnDidReceive;
    private static readonly WillPresentDelegate WillPresent = OnWillPresent;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_ready) return;
            try
            {
                _userNotifications = NativeLibrary.Load(
                    "/System/Library/Frameworks/UserNotifications.framework/UserNotifications");

                EnsureDelegateClass();
                _delegate = Msg(Msg(DelegateClass.Handle, Sel("alloc")), Sel("init"));
                if (_delegate == IntPtr.Zero) return;

                var center = CurrentCenter();
                if (center == IntPtr.Zero) return;

                Msg(center, Sel("setDelegate:"), _delegate);
                RegisterCategory(center);
                RequestAuthorization(center);
                _ready = true;
            }
            catch
            {
                _ready = false;
            }
        }
    }

    public static void Show(string title, string body, Uri? actionUri)
    {
        EnsureRegistered();
        if (!_ready) return;

        var pool = objc_autoreleasePoolPush();
        try
        {
            var id = Guid.NewGuid().ToString("N");
            if (actionUri is not null)
                PendingUris[id] = actionUri.AbsoluteUri;

            var content = Msg(Msg(Class("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
            var titleNs = NewNSString(title);
            var bodyNs = NewNSString(body);
            var catNs = NewNSString(CategoryId);
            Msg(content, Sel("setTitle:"), titleNs);
            Msg(content, Sel("setBody:"), bodyNs);
            Msg(content, Sel("setCategoryIdentifier:"), catNs);
            Msg(content, Sel("setSound:"), Msg(Class("UNNotificationSound"), Sel("defaultSound")));
            Release(titleNs);
            Release(bodyNs);
            Release(catNs);

            var idNs = NewNSString(id);
            var request = Msg(
                Class("UNNotificationRequest"),
                Sel("requestWithIdentifier:content:trigger:"),
                idNs, content, IntPtr.Zero);
            Release(idNs);
            Release(content);

            var center = CurrentCenter();
            Msg(center, Sel("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static void RegisterCategory(IntPtr center)
    {
        var showId = NewNSString(ShowActionId);
        var showTitle = NewNSString("Show");
        var action = Msg(
            Class("UNNotificationAction"),
            Sel("actionWithIdentifier:title:options:"),
            showId, showTitle, new UIntPtr(ActionForeground));
        Release(showId);
        Release(showTitle);

        var actions = Msg(Class("NSArray"), Sel("arrayWithObject:"), action);
        var intents = Msg(Class("NSArray"), Sel("array"));
        var catId = NewNSString(CategoryId);
        var category = Msg(
            Class("UNNotificationCategory"),
            Sel("categoryWithIdentifier:actions:intentIdentifiers:options:"),
            catId, actions, intents, new UIntPtr(0));
        Release(catId);

        var set = Msg(Class("NSSet"), Sel("setWithObject:"), category);
        Msg(center, Sel("setNotificationCategories:"), set);
    }

    private static void RequestAuthorization(IntPtr center)
    {
        // Fire-and-forget; macOS prompts once. Nil handler is accepted on current SDKs.
        Msg(center, Sel("requestAuthorizationWithOptions:completionHandler:"),
            new UIntPtr(AuthAlertSoundBadge), IntPtr.Zero);
    }

    private static IntPtr CurrentCenter() =>
        Msg(Class("UNUserNotificationCenter"), Sel("currentNotificationCenter"));

    private static void OnDidReceive(IntPtr self, IntPtr cmd, IntPtr center, IntPtr response, IntPtr completion)
    {
        try
        {
            var actionId = ToManagedString(Msg(response, Sel("actionIdentifier"))) ?? "";
            // Ignore explicit dismiss; handle body click + Show the same way.
            if (actionId.Equals("com.apple.UNNotificationDismissActionIdentifier", StringComparison.Ordinal))
                return;

            var notification = Msg(response, Sel("notification"));
            var request = Msg(notification, Sel("request"));
            var id = ToManagedString(Msg(request, Sel("identifier")));
            if (id is not null && PendingUris.TryRemove(id, out var uri) && !string.IsNullOrWhiteSpace(uri))
                OpenDeepLink(uri);
            else
                ActivateMeetJoe();
        }
        catch
        {
            // Notification handoff is best-effort.
        }
        finally
        {
            InvokeVoidBlock(completion);
        }
    }

    private static void OnWillPresent(IntPtr self, IntPtr cmd, IntPtr center, IntPtr notification, IntPtr completion)
    {
        InvokePresentationBlock(completion, PresentBannerSoundList);
    }

    private static void OpenDeepLink(string uri)
    {
        // In-process handoff: MainViewModel's drop watcher / Activated opens the chat.
        DeepLinkDrop.Write(uri);
        ActivateMeetJoe();
    }

    private static void ActivateMeetJoe()
    {
        var app = Msg(Class("NSApplication"), Sel("sharedApplication"));
        if (app == IntPtr.Zero) return;
        // activateIgnoringOtherApps: — BOOL YES
        MsgByte(app, Sel("activateIgnoringOtherApps:"), 1);
    }

    private static void InvokeVoidBlock(IntPtr block)
    {
        if (block == IntPtr.Zero) return;
        var invoke = Marshal.ReadIntPtr(block, 16);
        Marshal.GetDelegateForFunctionPointer<VoidBlockInvoke>(invoke)(block);
    }

    private static void InvokePresentationBlock(IntPtr block, nuint options)
    {
        if (block == IntPtr.Zero) return;
        var invoke = Marshal.ReadIntPtr(block, 16);
        Marshal.GetDelegateForFunctionPointer<PresentationBlockInvoke>(invoke)(block, options);
    }

    private static void EnsureDelegateClass()
    {
        _ = DelegateClass.Handle;
    }

    private static class DelegateClass
    {
        public static readonly IntPtr Handle = Create();

        private static IntPtr Create()
        {
            var existing = Class("MeetJoeNotificationDelegate");
            if (existing != IntPtr.Zero) return existing;

            var cls = objc_allocateClassPair(Class("NSObject"), "MeetJoeNotificationDelegate", 0);
            class_addMethod(cls, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                Marshal.GetFunctionPointerForDelegate(DidReceive), "v@:@@@");
            class_addMethod(cls, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                Marshal.GetFunctionPointerForDelegate(WillPresent), "v@:@@@");

            var protocol = objc_getProtocol("UNUserNotificationCenterDelegate");
            if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);

            objc_registerClassPair(cls);
            return cls;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidReceiveDelegate(IntPtr self, IntPtr cmd, IntPtr center, IntPtr response, IntPtr completion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WillPresentDelegate(IntPtr self, IntPtr cmd, IntPtr center, IntPtr notification, IntPtr completion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidBlockInvoke(IntPtr block);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PresentationBlockInvoke(IntPtr block, nuint options);

    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr NewNSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Msg(Msg(Class("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static string? ToManagedString(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(Msg(obj, Sel("UTF8String")));
    }

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) Msg(obj, Sel("release"));
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(ObjC)] private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
    [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a, IntPtr b, UIntPtr c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, IntPtr a, IntPtr b, IntPtr c, UIntPtr d);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MsgByte(IntPtr r, IntPtr s, byte a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s, UIntPtr a, IntPtr b);
}
#endif
