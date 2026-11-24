using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeetJoe.Contracts;

namespace MeetJoe.App.Services;

/// <summary>
/// Signs in with the OAuth password grant and keeps the access token fresh. Tokens are held in
/// memory only, so the user signs in again on each launch.
/// </summary>
public sealed class AuthService(HttpClient http) : IAuthService
{
    // Refresh this long before expiry so a request never goes out with a token about to lapse.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Tokens? _tokens;

    public event Action? SessionExpired;

    public bool IsSignedIn => _tokens is not null;

    public async Task SignInAsync(string email, string password, CancellationToken ct = default)
    {
        _tokens = await RequestAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = AuthConstants.DesktopClientId,
                ["scope"] = "offline_access",
                ["username"] = email,
                ["password"] = password,
            },
            ct);
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        var tokens = _tokens;
        if (tokens is null) return null;
        if (tokens.ExpiresAt - DateTimeOffset.UtcNow > RefreshMargin) return tokens.AccessToken;

        await _gate.WaitAsync();
        try
        {
            tokens = _tokens;
            if (tokens is null) return null;
            if (tokens.ExpiresAt - DateTimeOffset.UtcNow > RefreshMargin) return tokens.AccessToken; // Another caller refreshed.
            if (tokens.RefreshToken is null) return Expire();

            try
            {
                _tokens = await RequestAsync(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = AuthConstants.DesktopClientId,
                        ["refresh_token"] = tokens.RefreshToken,
                    },
                    CancellationToken.None);
                return _tokens.AccessToken;
            }
            catch (ApiException ex) when (ex.StatusCode is >= 400 and < 500)
            {
                return Expire(); // The server rejected the refresh token; only signing in again helps.
            }
            catch (HttpRequestException)
            {
                // Server unreachable. The current token may still be accepted; let the caller try.
                return tokens.ExpiresAt > DateTimeOffset.UtcNow ? tokens.AccessToken : null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SignOut() => _tokens = null;

    private string? Expire()
    {
        _tokens = null;
        SessionExpired?.Invoke();
        return null;
    }

    private async Task<Tokens> RequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync(AuthConstants.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadAsync<TokenError>(response, ct);
            throw new ApiException(
                error?.Description ?? "Sign-in failed. Try again.", (int)response.StatusCode);
        }

        var token = await TryReadAsync<TokenResponse>(response, ct);
        if (token?.AccessToken is null) throw new ApiException("The server returned an unexpected sign-in response.");
        return new Tokens(token.AccessToken, token.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record Tokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record TokenError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);
}
