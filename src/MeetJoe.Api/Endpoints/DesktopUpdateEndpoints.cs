using MeetJoe.Api.Updates;

namespace MeetJoe.Api.Endpoints;

internal static class DesktopUpdateEndpoints
{
    public static void MapDesktopUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        // Public — installed clients check before sign-in.
        var group = app.MapGroup("/api/desktop");

        group.MapGet("/updates", (string? rid, HttpRequest request, IDesktopUpdateFeed feed) =>
        {
            rid = string.IsNullOrWhiteSpace(rid) ? DetectRid(request) : rid.Trim();
            var latest = feed.GetLatest(rid, request);
            return latest is null
                ? Results.NotFound(new { message = $"No desktop release published for '{rid}'." })
                : Results.Ok(latest);
        });

        group.MapGet("/updates/rids", (IDesktopUpdateFeed feed) => Results.Ok(feed.ListRids()));
    }

    private static string DetectRid(HttpRequest request)
    {
        var hint = request.Headers.UserAgent.ToString();
        if (hint.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                is System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (hint.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return "linux-x64";
        if (hint.Contains("Mac", StringComparison.OrdinalIgnoreCase) || hint.Contains("Darwin", StringComparison.OrdinalIgnoreCase))
            return hint.Contains("ARM", StringComparison.OrdinalIgnoreCase) || hint.Contains("Apple", StringComparison.OrdinalIgnoreCase)
                ? "osx-arm64"
                : "osx-x64";
        return "linux-x64";
    }
}
