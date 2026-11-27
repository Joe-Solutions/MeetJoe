using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeetJoe.App.Services.Spotify;

/// <summary>Spotify Authorization Code + PKCE via loopback HTTP redirect.</summary>
public sealed class SpotifyAuthService : IDisposable
{
    public const string Scopes =
        "user-read-private user-read-email " +
        "playlist-read-private playlist-read-collaborative " +
        "user-library-read user-read-recently-played " +
        "user-read-playback-state user-modify-playback-state user-read-currently-playing";

    private static readonly Uri AuthorizeEndpoint = new("https://accounts.spotify.com/authorize");
    private static readonly Uri TokenEndpoint = new("https://accounts.spotify.com/api/token");

    private readonly string _clientId;
    private readonly ISpotifyTokenStore _store;
    private readonly Func<Uri, Task> _openUri;
    private readonly HttpClient _http;
    private readonly object _gate = new();

    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;
    private string? _refreshToken;

    public SpotifyAuthService(
        string clientId,
        ISpotifyTokenStore store,
        Func<Uri, Task> openUri,
        HttpClient? http = null)
    {
        _clientId = clientId?.Trim() ?? string.Empty;
        _store = store;
        _openUri = openUri;
        _http = http ?? new HttpClient();
        _refreshToken = store.LoadRefreshToken();
    }

    public bool HasClientId => !string.IsNullOrWhiteSpace(_clientId);
    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(_refreshToken);
    public bool IsConnected => HasRefreshToken;

    /// <summary>Current refresh token for publishing to MeetJoe shared testing mode.</summary>
    public string? RefreshToken
    {
        get { lock (_gate) return _refreshToken; }
    }

    public void Dispose() => _http.Dispose();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!HasClientId)
            throw new InvalidOperationException(
                "Set MEETJOE_SPOTIFY_CLIENT_ID to your Spotify Developer app Client ID.");

        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var listener = new HttpListener();
        var port = PickFreePort();
        // Register http://127.0.0.1/callback (no port) in the Spotify dashboard; port is dynamic.
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        listener.Prefixes.Add(redirectUri + "/");
        listener.Start();

        var authUrl = new UriBuilder(AuthorizeEndpoint)
        {
            Query = string.Join('&',
                "response_type=code",
                "client_id=" + Uri.EscapeDataString(_clientId),
                "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                "scope=" + Uri.EscapeDataString(Scopes),
                "state=" + Uri.EscapeDataString(state),
                "code_challenge_method=S256",
                "code_challenge=" + Uri.EscapeDataString(challenge)),
        }.Uri;

        await _openUri(authUrl);

        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { /* ignore */ }
        });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var query = context.Request.QueryString;
        var code = query["code"];
        var returnedState = query["state"];
        var error = query["error"];

        var html = error is null && code is not null
            ? "<!doctype html><html><body style=\"font-family:system-ui;padding:2rem\">" +
              "<h1>Connected to MeetJoe</h1><p>You can close this window and return to the app.</p></body></html>"
            : "<!doctype html><html><body style=\"font-family:system-ui;padding:2rem\">" +
              "<h1>Could not connect</h1><p>Return to MeetJoe and try again.</p></body></html>";

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct);
        context.Response.Close();
        listener.Stop();

        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException("Spotify authorization was denied: " + error);
        if (string.IsNullOrEmpty(code) || !string.Equals(returnedState, state, StringComparison.Ordinal))
            throw new InvalidOperationException("Spotify authorization failed (invalid response).");

        await ExchangeCodeAsync(code, redirectUri, verifier, ct);
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            _accessToken = null;
            _refreshToken = null;
            _accessExpiresAt = default;
        }

        _store.Clear();
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiresAt.AddMinutes(-1))
                return _accessToken;
        }

        if (string.IsNullOrWhiteSpace(_refreshToken)) return null;
        try
        {
            await RefreshAsync(ct);
        }
        catch
        {
            return null;
        }

        lock (_gate) return _accessToken;
    }

    private async Task ExchangeCodeAsync(string code, string redirectUri, string verifier, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _clientId,
            ["code_verifier"] = verifier,
        });

        using var response = await _http.PostAsync(TokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Spotify token exchange failed: " + Truncate(body));

        ApplyTokenResponse(body);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var refresh = _refreshToken;
        if (string.IsNullOrWhiteSpace(refresh))
            throw new InvalidOperationException("Not connected to Spotify.");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = _clientId,
        });

        using var response = await _http.PostAsync(TokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Disconnect();
            throw new InvalidOperationException("Spotify session expired. Connect again.");
        }

        ApplyTokenResponse(body);
    }

    private void ApplyTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("Spotify token response missing access_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        lock (_gate)
        {
            _accessToken = access;
            _accessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            if (!string.IsNullOrWhiteSpace(refresh))
            {
                _refreshToken = refresh;
                _store.SaveRefreshToken(refresh);
            }
            else if (!string.IsNullOrWhiteSpace(_refreshToken))
            {
                _store.SaveRefreshToken(_refreshToken);
            }
        }
    }

    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int PickFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}
