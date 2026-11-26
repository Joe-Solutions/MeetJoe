namespace MeetJoe.Desktop;

internal static class DeepLinkParser
{
    /// <summary>Extracts the invite token from meetjoe://join?token=…</summary>
    public static string? TryParseJoinToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("meetjoe", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals("token", StringComparison.OrdinalIgnoreCase)
                && pair[1].Length > 0)
                return Uri.UnescapeDataString(pair[1]);
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("join", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(segments[1]);

        return null;
    }
}
