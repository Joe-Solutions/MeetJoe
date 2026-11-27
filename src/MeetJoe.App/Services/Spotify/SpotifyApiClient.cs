using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MeetJoe.App.Services.Spotify;

/// <summary>Thin Spotify Web API client for Music v1 (library + Connect control).</summary>
public sealed class SpotifyApiClient(SpotifyAuthService auth, HttpClient? http = null) : IDisposable
{
    private static readonly Uri ApiRoot = new("https://api.spotify.com/v1/");
    private readonly HttpClient _http = http ?? new HttpClient { BaseAddress = ApiRoot };

    public void Dispose() => _http.Dispose();

    public async Task<SpotifyUser> GetMeAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("me", ct);
        var root = doc.RootElement;
        return new SpotifyUser(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? "Spotify user"
                : "Spotify user",
            root.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                ? email.GetString()
                : null,
            FirstImageUrl(root));
    }

    public async Task<IReadOnlyList<SpotifyPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var list = new List<SpotifyPlaylist>();
        var path = "me/playlists?limit=50";
        while (path is not null)
        {
            using var doc = await GetJsonAsync(path, ct);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                list.Add(ParsePlaylist(item));
            }

            path = NextPath(doc.RootElement);
        }

        return list;
    }

    public async Task<IReadOnlyList<SpotifyTrack>> GetLikedTracksAsync(int limit = 50, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"me/tracks?limit={Math.Clamp(limit, 1, 50)}", ct);
        var tracks = new List<SpotifyTrack>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseTrack(track);
                if (parsed is not null) tracks.Add(parsed);
            }
        }

        return tracks;
    }

    public async Task<IReadOnlyList<SpotifyTrack>> GetRecentlyPlayedAsync(int limit = 30, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"me/player/recently-played?limit={Math.Clamp(limit, 1, 50)}", ct);
        var tracks = new List<SpotifyTrack>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (!item.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object)
                continue;
            var parsed = ParseTrack(track);
            if (parsed is null || !seen.Add(parsed.Id)) continue;
            tracks.Add(parsed);
        }

        return tracks;
    }

    public async Task<IReadOnlyList<SpotifyTrack>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
    {
        var tracks = new List<SpotifyTrack>();
        var path = $"playlists/{Uri.EscapeDataString(playlistId)}/tracks?limit=50";
        while (path is not null)
        {
            using var doc = await GetJsonAsync(path, ct);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
                {
                    var parsed = ParseTrack(track);
                    if (parsed is not null) tracks.Add(parsed);
                }
            }

            path = NextPath(doc.RootElement);
        }

        return tracks;
    }

    public async Task<SpotifySearchResults> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0)
            return new SpotifySearchResults([], []);

        using var doc = await GetJsonAsync(
            "search?type=track,playlist&limit=20&q=" + Uri.EscapeDataString(q), ct);

        var tracks = new List<SpotifyTrack>();
        if (doc.RootElement.TryGetProperty("tracks", out var tracksNode)
            && tracksNode.TryGetProperty("items", out var trackItems))
        {
            foreach (var item in trackItems.EnumerateArray())
            {
                var parsed = ParseTrack(item);
                if (parsed is not null) tracks.Add(parsed);
            }
        }

        var playlists = new List<SpotifyPlaylist>();
        if (doc.RootElement.TryGetProperty("playlists", out var playlistsNode)
            && playlistsNode.TryGetProperty("items", out var playlistItems))
        {
            foreach (var item in playlistItems.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    playlists.Add(ParsePlaylist(item));
            }
        }

        return new SpotifySearchResults(tracks, playlists);
    }

    public async Task<SpotifyPlaybackState?> GetPlaybackAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "me/player", null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Spotify playback: " + Truncate(body));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        SpotifyTrack? track = null;
        if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            track = ParseTrack(item);

        var deviceName = root.TryGetProperty("device", out var device)
                         && device.TryGetProperty("name", out var dn)
            ? dn.GetString()
            : null;

        return new SpotifyPlaybackState(
            root.TryGetProperty("is_playing", out var playing) && playing.GetBoolean(),
            track,
            deviceName,
            root.TryGetProperty("progress_ms", out var progress) ? progress.GetInt32() : 0);
    }

    public Task PlayAsync(string? spotifyUri = null, CancellationToken ct = default)
    {
        HttpContent? content = null;
        if (!string.IsNullOrWhiteSpace(spotifyUri))
        {
            var json = spotifyUri.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? $"{{\"uris\":[\"{EscapeJson(spotifyUri)}\"]}}"
                : $"{{\"context_uri\":\"{EscapeJson(spotifyUri)}\"}}";
            content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return SendNoContentAsync(HttpMethod.Put, "me/player/play", content, ct);
    }

    public Task PauseAsync(CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Put, "me/player/pause", null, ct);

    public Task NextAsync(CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, "me/player/next", null, ct);

    public Task PreviousAsync(CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, "me/player/previous", null, ct);

    private async Task SendNoContentAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, content, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "No active Spotify device. Open Spotify on your phone or computer, then try again.");
        if ((int)response.StatusCode == 403)
            throw new InvalidOperationException(
                "Spotify Premium is required for playback control, or Spotify denied this action.");
        throw new InvalidOperationException("Spotify player: " + Truncate(body));
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, relativePath, null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Spotify API: " + Truncate(body));
        return JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string relativePath, HttpContent? content, CancellationToken ct)
    {
        var token = await auth.GetAccessTokenAsync(ct)
                    ?? throw new InvalidOperationException("Connect Spotify first.");

        // Absolute Spotify next URLs come back from paging; strip the host.
        if (relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(relativePath, UriKind.Absolute, out var absolute)
            && absolute.Host.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = absolute.PathAndQuery.TrimStart('/');
            if (relativePath.StartsWith("v1/", StringComparison.Ordinal))
                relativePath = relativePath[3..];
        }

        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;
        return await _http.SendAsync(request, ct);
    }

    private static string? NextPath(JsonElement root)
    {
        if (!root.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
            return null;
        return next.GetString();
    }

    private static SpotifyPlaylist ParsePlaylist(JsonElement item)
    {
        var tracks = 0;
        if (item.TryGetProperty("tracks", out var t) && t.TryGetProperty("total", out var total))
            tracks = total.GetInt32();

        return new SpotifyPlaylist(
            item.GetProperty("id").GetString()!,
            item.TryGetProperty("name", out var name) ? name.GetString() ?? "Playlist" : "Playlist",
            item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                ? desc.GetString()
                : null,
            FirstImageUrl(item),
            tracks,
            item.TryGetProperty("uri", out var uri) ? uri.GetString() ?? string.Empty : string.Empty);
    }

    private static SpotifyTrack? ParseTrack(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        var id = idEl.GetString();
        if (string.IsNullOrWhiteSpace(id)) return null;

        var artists = "Unknown";
        if (item.TryGetProperty("artists", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array)
        {
            var names = artistsEl.EnumerateArray()
                .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();
            if (names.Length > 0) artists = string.Join(", ", names);
        }

        string? album = null;
        string? image = FirstImageUrl(item);
        if (item.TryGetProperty("album", out var albumEl) && albumEl.ValueKind == JsonValueKind.Object)
        {
            album = albumEl.TryGetProperty("name", out var an) ? an.GetString() : null;
            image ??= FirstImageUrl(albumEl);
        }

        return new SpotifyTrack(
            id,
            item.TryGetProperty("name", out var name) ? name.GetString() ?? "Track" : "Track",
            artists,
            album,
            image,
            item.TryGetProperty("uri", out var uri) ? uri.GetString() ?? $"spotify:track:{id}" : $"spotify:track:{id}",
            item.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0);
    }

    private static string? FirstImageUrl(JsonElement item)
    {
        if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var image in images.EnumerateArray())
        {
            if (image.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return url.GetString();
        }

        return null;
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Truncate(string value) =>
        value.Length <= 240 ? value : value[..240] + "…";
}
