#if OSX
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Foundation;
using ObjCRuntime;

namespace MeetJoe.Desktop;

/// <summary>
/// Teams-style macOS TCC: call AVCaptureDevice.requestAccessForMediaType so the
/// system dialog appears and MeetJoe shows up under Privacy → Microphone / Camera.
/// WKWebView UIDelegate Grant alone does not register the host app with TCC.
/// </summary>
internal static class MacOsMediaPermissions
{
    private const nint StatusNotDetermined = 0;
    private const nint StatusAuthorized = 3;
    // AVMediaType* FourCCs used by AVFoundation.
    private const string MediaVideo = "vide";
    private const string MediaAudio = "soun";

    // ObjC BOOL is a signed byte — a C# bool block signature will not be invoked correctly.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AccessCallback(byte granted);

    private static int _avFoundationLoaded;

    public static Task<bool> EnsureAsync(string kind)
    {
        var mediaType = kind.Equals("mic", StringComparison.OrdinalIgnoreCase) ? MediaAudio : MediaVideo;
        if (Dispatcher.UIThread.CheckAccess())
            return BeginRequest(mediaType);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            BeginRequest(mediaType).ContinueWith(
                t => tcs.TrySetResult(t is { IsCompletedSuccessfully: true, Result: true }),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        });
        return tcs.Task;
    }

    private static void EnsureAvFoundationLoaded()
    {
        if (Interlocked.Exchange(ref _avFoundationLoaded, 1) == 1) return;
        // Without this, Class.GetHandle("AVCaptureDevice") is often null in a .NET host.
        Dlfcn.dlopen("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", 0);
    }

    private static Task<bool> BeginRequest(string mediaType)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockLiteral block = default;
        AccessCallback? callback = null;
        var cleaned = 0;

        void Complete(bool granted)
        {
            if (Interlocked.Exchange(ref cleaned, 1) == 0)
            {
                try { block.CleanupBlock(); }
                catch { /* ignore */ }
            }

            tcs.TrySetResult(granted);
        }

        try
        {
            EnsureAvFoundationLoaded();

            var cls = Class.GetHandle("AVCaptureDevice");
            if (cls == IntPtr.Zero)
            {
                Complete(false);
                return tcs.Task;
            }

            using var media = new NSString(mediaType);
            var statusSel = Selector.GetHandle("authorizationStatusForMediaType:");
            var status = nint_objc_msgSend_IntPtr(cls, statusSel, media.Handle);

            if (status == StatusAuthorized)
            {
                Complete(true);
                return tcs.Task;
            }

            if (status != StatusNotDetermined)
            {
                // Denied / Restricted — System Settings is the only remaining path.
                Complete(false);
                return tcs.Task;
            }

            // Keep the block alive until macOS invokes the completion handler.
            // Cleaning it up in a finally (previous bug) prevented the system dialog
            // and left MeetJoe absent from Privacy → Microphone / Camera.
            callback = granted => Complete(granted != 0);
            block = new BlockLiteral();
#pragma warning disable CS8625
            block.SetupBlock(callback, null);
#pragma warning restore CS8625

            var requestSel = Selector.GetHandle("requestAccessForMediaType:completionHandler:");
            void_objc_msgSend_IntPtr_Block(cls, requestSel, media.Handle, ref block);

            // Safety: if the callback never fires, do not hang call join forever.
            _ = Task.Delay(TimeSpan.FromSeconds(120)).ContinueWith(_ => Complete(false));

            // Keep callback rooted for the lifetime of the request.
            GC.KeepAlive(callback);
        }
        catch
        {
            Complete(false);
        }

        return tcs.Task;
    }

    [DllImport(Constants.ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint nint_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(Constants.ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend_IntPtr_Block(
        IntPtr receiver, IntPtr selector, IntPtr arg1, ref BlockLiteral block);
}
#endif
