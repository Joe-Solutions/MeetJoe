namespace MeetJoe.App.Services.Spotify;

/// <summary>
/// Desktop-only Spotify refresh token. Implementations must not throw; a failed save
/// just means Connect Spotify again next launch.
/// </summary>
public interface ISpotifyTokenStore
{
    string? LoadRefreshToken();
    void SaveRefreshToken(string refreshToken);
    void Clear();
}

/// <summary>Memory only — tests and platforms without a durable store.</summary>
public sealed class MemorySpotifyTokenStore : ISpotifyTokenStore
{
    private string? _token;

    public string? LoadRefreshToken() => _token;
    public void SaveRefreshToken(string refreshToken) => _token = refreshToken;
    public void Clear() => _token = null;
}

/// <summary>
/// Plain file under LocalApplicationData/MeetJoe/spotify-refresh.bin (0600 on Unix).
/// Keychain/DPAPI wiring can replace this later without changing callers.
/// </summary>
public sealed class FileSpotifyTokenStore : ISpotifyTokenStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetJoe",
        "spotify-refresh.bin");

    public string? LoadRefreshToken()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = File.ReadAllBytes(FilePath);
            return bytes.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes);
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
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(FilePath, System.Text.Encoding.UTF8.GetBytes(refreshToken));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best-effort */ }
            }
        }
        catch
        {
            // Not saved: the user connects Spotify again next launch.
        }
    }

    public void Clear()
    {
        try { File.Delete(FilePath); }
        catch { /* Nothing to clear. */ }
    }
}
