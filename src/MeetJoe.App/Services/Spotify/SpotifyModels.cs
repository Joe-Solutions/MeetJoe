namespace MeetJoe.App.Services.Spotify;

public sealed record SpotifyUser(string Id, string DisplayName, string? Email, string? ImageUrl);

public sealed record SpotifyPlaylist(
    string Id,
    string Name,
    string? Description,
    string? ImageUrl,
    int TrackCount,
    string Uri);

public sealed record SpotifyTrack(
    string Id,
    string Name,
    string Artists,
    string? AlbumName,
    string? ImageUrl,
    string Uri,
    int DurationMs);

public sealed record SpotifyPlaybackState(
    bool IsPlaying,
    SpotifyTrack? Track,
    string? DeviceName,
    int ProgressMs);

public sealed record SpotifySearchResults(
    IReadOnlyList<SpotifyTrack> Tracks,
    IReadOnlyList<SpotifyPlaylist> Playlists);
