using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MeetJoe.Api.Music;

public sealed class SharedSpotifyOptions
{
    public const string SectionName = "Spotify";

    /// <summary>Public Client ID (PKCE). Defaults to MeetJoe's Spotify app.</summary>
    public string ClientId { get; set; } = "beba5aebc4004b36be98cbc5a0de40b2";

    /// <summary>Optional bootstrap refresh token from env (testing).</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Where a published refresh token is stored (api-keys volume in prod).</summary>
    public string SharedStorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetJoe",
        "spotify-shared.json");
}

/// <summary>
/// Test-only: one Spotify Premium refresh token shared with every signed-in MeetJoe user.
/// </summary>
public sealed class SharedSpotifyService(IOptions<SharedSpotifyOptions> options, IHttpClientFactory httpFactory)
{
    private readonly object _gate = new();
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;
    private string? _displayName;

    public bool IsConfigured
    {
        get
        {
            var refresh = LoadRefreshToken();
            return !string.IsNullOrWhiteSpace(refresh)
                   && !string.IsNullOrWhiteSpace(options.Value.ClientId);
        }
    }

    public async Task<(bool Available, string? AccessToken, int ExpiresIn, string? DisplayName)> GetSessionAsync(
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return (false, null, 0, null);

        lock (_gate)
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiresAt.AddMinutes(-1))
                return (true, _accessToken, (int)Math.Max(60, (_accessExpiresAt - DateTimeOffset.UtcNow).TotalSeconds), _displayName);
        }

        await RefreshAccessTokenAsync(ct);

        lock (_gate)
        {
            return _accessToken is null
                ? (false, null, 0, null)
                : (true, _accessToken, (int)Math.Max(60, (_accessExpiresAt - DateTimeOffset.UtcNow).TotalSeconds), _displayName);
        }
    }

    public Task PublishAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Refresh token is required.");

        var path = options.Value.SharedStorePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(new StoreFile(refreshToken.Trim(), DateTimeOffset.UtcNow));
        File.WriteAllText(path, json);

        lock (_gate)
        {
            _accessToken = null;
            _accessExpiresAt = default;
            _displayName = null;
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        try
        {
            var path = options.Value.SharedStorePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best-effort
        }

        lock (_gate)
        {
            _accessToken = null;
            _accessExpiresAt = default;
            _displayName = null;
        }

        return Task.CompletedTask;
    }

    private string? LoadRefreshToken()
    {
        var fromEnv = options.Value.RefreshToken?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        try
        {
            var path = options.Value.SharedStorePath;
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(file?.RefreshToken) ? null : file.RefreshToken.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        var refresh = LoadRefreshToken()
                      ?? throw new InvalidOperationException("Shared Spotify is not configured.");
        var clientId = options.Value.ClientId;

        var http = httpFactory.CreateClient(nameof(SharedSpotifyService));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = clientId,
        });

        using var response = await http.PostAsync("https://accounts.spotify.com/api/token", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Spotify token refresh failed: " + Truncate(body));

        using var doc = JsonDocument.Parse(body);
        var access = doc.RootElement.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("Missing access_token.");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (!string.IsNullOrWhiteSpace(newRefresh))
            await PublishAsync(newRefresh, ct);

        string? display = null;
        try
        {
            using var meReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var meRes = await http.SendAsync(meReq, ct);
            if (meRes.IsSuccessStatusCode)
            {
                using var meDoc = JsonDocument.Parse(await meRes.Content.ReadAsStringAsync(ct));
                if (meDoc.RootElement.TryGetProperty("display_name", out var name)
                    && name.ValueKind == JsonValueKind.String)
                    display = name.GetString();
            }
        }
        catch
        {
            // optional
        }

        lock (_gate)
        {
            _accessToken = access;
            _accessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            _displayName = display ?? "Shared Spotify";
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";

    private sealed record StoreFile(string RefreshToken, DateTimeOffset UpdatedAt);
}
