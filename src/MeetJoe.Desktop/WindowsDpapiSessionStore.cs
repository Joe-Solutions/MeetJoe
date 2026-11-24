using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using MeetJoe.App.Services;

namespace MeetJoe.Desktop;

/// <summary>
/// Refresh token in %LOCALAPPDATA%\MeetJoe\session.bin, encrypted with DPAPI for the current
/// Windows user. Outside the Velopack install dir, so updates and reinstalls keep it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiSessionStore : ISessionStore
{
    private static readonly byte[] Entropy = "MeetJoe.Session.v1"u8.ToArray();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetJoe", "session.bin");

    public string? LoadRefreshToken()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
        }
        catch
        {
            // Not saved: the user signs in again next launch.
        }
    }

    public void Clear()
    {
        try { File.Delete(FilePath); }
        catch { /* Nothing to clear. */ }
    }
}
