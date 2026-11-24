#if OSX
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Foundation;
using ObjCRuntime;

namespace MeetJoe.Desktop;

/// <summary>
/// Triggers the real macOS TCC camera/mic dialog via AVFoundation.
/// WKWebView Grant alone is not enough — the host process must be authorized,
/// and the process must run from an .app bundle that includes Info.plist usage strings
/// plus camera/mic entitlements when signed with hardened runtime.
/// </summary>
internal static class MacOsMediaPermissions
{
    private const nint StatusNotDetermined = 0;
    private const nint StatusAuthorized = 3;
    private const string MediaVideo = "vide";
    private const string MediaAudio = "soun";

    private delegate void AccessCallback(bool granted);

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

    private static Task<bool> BeginRequest(string mediaType)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var cls = Class.GetHandle("AVCaptureDevice");
            if (cls == IntPtr.Zero)
            {
                tcs.TrySetResult(false);
                return tcs.Task;
            }

            using var media = new NSString(mediaType);
            var statusSel = Selector.GetHandle("authorizationStatusForMediaType:");
            var status = nint_objc_msgSend_IntPtr(cls, statusSel, media.Handle);

            if (status == StatusAuthorized)
            {
                tcs.TrySetResult(true);
                return tcs.Task;
            }

            if (status != StatusNotDetermined)
            {
                // Denied / Restricted — macOS will not show another dialog.
                tcs.TrySetResult(false);
                return tcs.Task;
            }

            AccessCallback callback = granted => tcs.TrySetResult(granted);
            var block = new BlockLiteral();
#pragma warning disable CS8625 // SetupBlock's second arg is an optional copy/dispose helper.
            block.SetupBlock(callback, null);
#pragma warning restore CS8625
            try
            {
                var requestSel = Selector.GetHandle("requestAccessForMediaType:completionHandler:");
                void_objc_msgSend_IntPtr_Block(cls, requestSel, media.Handle, ref block);
            }
            finally
            {
                block.CleanupBlock();
            }
        }
        catch
        {
            tcs.TrySetResult(false);
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
