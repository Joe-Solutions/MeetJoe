using MeetJoe.Api.Music;
using MeetJoe.Contracts;

namespace MeetJoe.Api.Endpoints;

internal static class MusicEndpoints
{
    public static void MapMusicEndpoints(this RouteGroupBuilder api)
    {
        var music = api.MapGroup("/music/spotify");

        music.MapGet("/session", async (SharedSpotifyService spotify, CancellationToken ct) =>
        {
            var (available, token, expires, display) = await spotify.GetSessionAsync(ct);
            return Results.Ok(new SharedSpotifySessionDto(
                available,
                token,
                expires,
                display,
                available ? "shared" : "none"));
        });

        music.MapPut("/shared", async (PublishSharedSpotifyRequest body, SharedSpotifyService spotify, CancellationToken ct) =>
        {
            await spotify.PublishAsync(body.RefreshToken, ct);
            var (available, token, expires, display) = await spotify.GetSessionAsync(ct);
            return Results.Ok(new SharedSpotifySessionDto(
                available,
                token,
                expires,
                display,
                "shared"));
        });

        music.MapDelete("/shared", async (SharedSpotifyService spotify, CancellationToken ct) =>
        {
            await spotify.ClearAsync(ct);
            return Results.Ok(new SharedSpotifySessionDto(false, null, 0, null, "none"));
        });
    }
}
