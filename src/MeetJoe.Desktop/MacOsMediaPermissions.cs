#if OSX
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MeetJoe.Desktop;

/// <summary>
/// Teams-style macOS TCC: call AVCaptureDevice.requestAccessForMediaType so the
/// system dialog appears and MeetJoe shows up under Privacy → Microphone / Camera.
/// WKWebView UIDelegate Grant alone does not register the host app with TCC.
/// </summary>
/// <remarks>
/// Pure libobjc P/Invoke on purpose. The bundled Xamarin.Mac has no native runtime
/// (no libxammac), so its NSString / BlockLiteral.SetupBlock threw inside our catch:
/// tccd never saw a request and MeetJoe never appeared in Privacy settings.
/// </remarks>
internal static class MacOsMediaPermissions
{
    private const nint StatusNotDetermined = 0;
    private const nint StatusAuthorized = 3;
    private const int BlockIsGlobal = 1 << 28;

    // Invoked by AVFoundation on an arbitrary queue: (block self, BOOL granted).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockInvoke(IntPtr block, byte granted);

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLayout
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    private static readonly object Gate = new();
    // Rooted forever: the function pointer handed to ObjC must outlive every request.
    private static readonly BlockInvoke InvokeDelegate = OnBlockInvoked;
    private static readonly ConcurrentDictionary<IntPtr, TaskCompletionSource<bool>> Pending = new();
    private static readonly Dictionary<string, IntPtr> Blocks = new();
    private static IntPtr _avFoundation;

    public static Task<bool> EnsureAsync(string kind)
    {
        var symbol = kind.Equals("mic", StringComparison.OrdinalIgnoreCase) ? "AVMediaTypeAudio" : "AVMediaTypeVideo";
        try
        {
            return Request(symbol);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static Task<bool> Request(string mediaSymbol)
    {
        IntPtr media, block;
        lock (Gate)
        {
            if (_avFoundation == IntPtr.Zero)
                _avFoundation = NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
            // AVMediaTypeAudio / AVMediaTypeVideo are exported NSString* constants.
            media = Marshal.ReadIntPtr(NativeLibrary.GetExport(_avFoundation, mediaSymbol));
            block = GetGlobalBlock(mediaSymbol);
        }

        var cls = objc_getClass("AVCaptureDevice");
        if (cls == IntPtr.Zero || media == IntPtr.Zero) return Task.FromResult(false);

        var status = nint_objc_msgSend_IntPtr(cls, sel_registerName("authorizationStatusForMediaType:"), media);
        if (status == StatusAuthorized) return Task.FromResult(true);
        // Denied / Restricted — System Settings is the only remaining path.
        if (status != StatusNotDetermined) return Task.FromResult(false);

        // One outstanding request per media type; mic + camera toggles share it.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Pending.GetOrAdd(block, tcs);
        if (!ReferenceEquals(existing, tcs)) return existing.Task;

        void_objc_msgSend_IntPtr_IntPtr(cls, sel_registerName("requestAccessForMediaType:completionHandler:"), media, block);

        // In a GUI app the completion block is not reliably invoked after the user answers
        // the dialog, so also poll the status until it leaves NotDetermined.
        _ = PollUntilDecidedAsync(cls, media, block);
        return tcs.Task;
    }

    private static async Task PollUntilDecidedAsync(IntPtr cls, IntPtr media, IntPtr block)
    {
        var statusSel = sel_registerName("authorizationStatusForMediaType:");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (Pending.ContainsKey(block) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(false);
            var status = nint_objc_msgSend_IntPtr(cls, statusSel, media);
            if (status != StatusNotDetermined)
            {
                Resolve(block, status == StatusAuthorized);
                return;
            }
        }

        // Safety: do not hang call join forever.
        Resolve(block, false);
    }

    private static void OnBlockInvoked(IntPtr block, byte granted) => Resolve(block, granted != 0);

    private static void Resolve(IntPtr block, bool granted)
    {
        if (Pending.TryRemove(block, out var tcs))
            tcs.TrySetResult(granted);
    }

    /// <summary>
    /// A static global block (like a non-capturing ObjC literal): Block_copy returns it
    /// as-is, so it never needs copy/dispose helpers. One per media type so the callback
    /// knows which request finished.
    /// </summary>
    private static IntPtr GetGlobalBlock(string key)
    {
        if (Blocks.TryGetValue(key, out var existing)) return existing;

        var libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<BlockDescriptor>());
        Marshal.StructureToPtr(new BlockDescriptor { Size = (nuint)Marshal.SizeOf<BlockLayout>() }, descriptor, false);

        var block = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLayout>());
        Marshal.StructureToPtr(new BlockLayout
        {
            Isa = NativeLibrary.GetExport(libSystem, "_NSConcreteGlobalBlock"),
            Flags = BlockIsGlobal,
            Invoke = Marshal.GetFunctionPointerForDelegate(InvokeDelegate),
            Descriptor = descriptor,
        }, block, false);

        Blocks[key] = block;
        return block;
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint nint_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
}
#endif
