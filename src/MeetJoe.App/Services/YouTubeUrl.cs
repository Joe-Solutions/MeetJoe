using System.Text.RegularExpressions;

namespace MeetJoe.App.Services;

/// <summary>Normalize pasted YouTube links into a watchable in-app URL.</summary>
public static partial class YouTubeUrl
{
    private static readonly Regex VideoIdPattern = VideoIdRegex();

    /// <summary>
    /// Accepts watch / youtu.be / shorts / embed / youtube.com browse URLs.
    /// Returns a https URL suitable for the in-app WebView.
    /// </summary>
    public static bool TryNormalize(string? input, out Uri url, out string displayTitle)
    {
        url = null!;
        displayTitle = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme is not ("http" or "https"))
            return false;

        var host = parsed.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (host is not ("youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtu.be"))
            return false;

        if (TryExtractVideoId(parsed, out var videoId))
        {
            url = new Uri($"https://www.youtube.com/watch?v={videoId}");
            displayTitle = "YouTube video";
            return true;
        }

        var builder = new UriBuilder(parsed) { Scheme = "https", Port = -1 };
        if (builder.Host.StartsWith("m.", StringComparison.OrdinalIgnoreCase)
            || builder.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            builder.Host = "www.youtube.com";

        url = builder.Uri;
        displayTitle = "YouTube";
        return true;
    }

    public static bool TryExtractVideoId(Uri uri, out string videoId)
    {
        videoId = string.Empty;
        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);

        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (IsVideoId(id))
            {
                videoId = id;
                return true;
            }
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("embed/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("live/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("v/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && IsVideoId(parts[1]))
            {
                videoId = parts[1];
                return true;
            }
        }

        var v = GetQueryValue(uri.Query, "v");
        if (IsVideoId(v))
        {
            videoId = v!;
            return true;
        }

        return false;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    private static bool IsVideoId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && VideoIdPattern.IsMatch(id);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();
}
