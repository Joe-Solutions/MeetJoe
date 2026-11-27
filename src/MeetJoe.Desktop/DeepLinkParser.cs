namespace MeetJoe.Desktop;

internal static class DeepLinkParser
{
    /// <summary>Extracts the invite token from meetjoe://join?token=…</summary>
    public static string? TryParseJoinToken(string? value)
    {
        if (!TryParseMeetJoeUri(value, out var uri)) return null;

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

        // Host-style: meetjoe://join?token=…
        if (uri.Host.Equals("join", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2
                    && pair[0].Equals("token", StringComparison.OrdinalIgnoreCase)
                    && pair[1].Length > 0)
                    return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    /// <summary>Extracts a conversation id from meetjoe://chat?id=…</summary>
    public static Guid? TryParseChatId(string? value)
    {
        if (!TryParseMeetJoeUri(value, out var uri)) return null;

        var hostIsChat = uri.Host.Equals("chat", StringComparison.OrdinalIgnoreCase);
        var pathIsChat = uri.AbsolutePath.Trim('/').StartsWith("chat", StringComparison.OrdinalIgnoreCase);
        if (!hostIsChat && !pathIsChat) return null;

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            if (!pair[0].Equals("id", StringComparison.OrdinalIgnoreCase)
                && !pair[0].Equals("conversationId", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Guid.TryParse(Uri.UnescapeDataString(pair[1]), out var id))
                return id;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2
            && segments[0].Equals("chat", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(Uri.UnescapeDataString(segments[1]), out var fromPath))
            return fromPath;

        return null;
    }

    private static bool TryParseMeetJoeUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
               && uri.Scheme.Equals("meetjoe", StringComparison.OrdinalIgnoreCase);
    }
}
