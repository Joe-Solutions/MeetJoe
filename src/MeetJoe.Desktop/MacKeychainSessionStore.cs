#if OSX
using System.Runtime.InteropServices;
using System.Text;
using MeetJoe.App.Services;

namespace MeetJoe.Desktop;

/// <summary>
/// Refresh token in the login Keychain (generic password). The item's ACL trusts the app's
/// designated requirement, which the pack script pins to the bundle id, so updates keep access.
/// </summary>
internal sealed class MacKeychainSessionStore : ISessionStore
{
    private const string Service = "com.joesolutions.meetjoe";
    private const string Account = "refresh-token";
    private const int ErrSecSuccess = 0;
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

    public string? LoadRefreshToken()
    {
        try
        {
            using var query = Query();
            query.Set(Sec("kSecReturnData"), Cf("kCFBooleanTrue"));
            query.Set(Sec("kSecMatchLimit"), Sec("kSecMatchLimitOne"));
            if (SecItemCopyMatching(query.Handle, out var data) != ErrSecSuccess || data == IntPtr.Zero)
                return null;
            try
            {
                var length = (int)CFDataGetLength(data);
                var bytes = new byte[length];
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CFRelease(data);
            }
        }
        catch
        {
            return null;
        }
    }

    public void SaveRefreshToken(string refreshToken)
    {
        try
        {
            Clear();
            var bytes = Encoding.UTF8.GetBytes(refreshToken);
            var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
            try
            {
                using var item = Query();
                item.Set(Sec("kSecValueData"), data);
                SecItemAdd(item.Handle, IntPtr.Zero);
            }
            finally
            {
                CFRelease(data);
            }
        }
        catch
        {
            // Not saved: the user signs in again next launch.
        }
    }

    public void Clear()
    {
        try
        {
            using var query = Query();
            SecItemDelete(query.Handle);
        }
        catch
        {
            // Nothing to clear.
        }
    }

    private static CfDictionary Query()
    {
        var dict = new CfDictionary();
        dict.Set(Sec("kSecClass"), Sec("kSecClassGenericPassword"));
        dict.SetString(Sec("kSecAttrService"), Service);
        dict.SetString(Sec("kSecAttrAccount"), Account);
        return dict;
    }

    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<IntPtr> Security = new(() => NativeLibrary.Load(SecurityLib));
    private static readonly Lazy<IntPtr> CoreFoundation = new(() => NativeLibrary.Load(CoreFoundationLib));

    // Exported CFTypeRef constants (kSecClass, kCFBooleanTrue, …) are pointers to the object.
    private static IntPtr Sec(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Security.Value, name));
    private static IntPtr Cf(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundation.Value, name));

    /// <summary>A CFMutableDictionary with CFType retain/release callbacks.</summary>
    private sealed class CfDictionary : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public CfDictionary()
        {
            Handle = CFDictionaryCreateMutable(IntPtr.Zero, 0,
                NativeLibrary.GetExport(CoreFoundation.Value, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(CoreFoundation.Value, "kCFTypeDictionaryValueCallBacks"));
        }

        public IntPtr Handle { get; }

        public void Set(IntPtr key, IntPtr value) => CFDictionarySetValue(Handle, key, value);

        public void SetString(IntPtr key, string value)
        {
            var str = CFStringCreateWithCString(IntPtr.Zero, value, Utf8);
            _owned.Add(str);
            Set(key, str);
        }

        public void Dispose()
        {
            foreach (var obj in _owned) CFRelease(obj);
            CFRelease(Handle);
        }
    }

    [DllImport(SecurityLib)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(SecurityLib)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(SecurityLib)] private static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CoreFoundationLib)] private static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundationLib)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr obj);
}
#endif
