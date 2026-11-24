namespace MeetJoe.App.Services;

/// <summary>Content-type and image detection for attachments — clients often upload as octet-stream.</summary>
internal static class FileTypes
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    public static bool IsImage(string? contentType, string? fileName)
    {
        if (contentType is not null
            && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !contentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
            return true;

        return fileName is not null && ImageExtensions.Contains(Path.GetExtension(fileName));
    }

    /// <summary>Best content type from file name and optional magic bytes. Resets the stream to the start.</summary>
    public static string ResolveContentType(string? fileName, Stream content)
    {
        if (IsImage(null, fileName))
            return FromExtension(fileName!) ?? "application/octet-stream";

        if (content.CanSeek)
        {
            var pos = content.Position;
            Span<byte> header = stackalloc byte[12];
            var read = content.Read(header);
            content.Position = pos;
            if (SniffImage(header[..read]) is { } sniffed)
                return sniffed;
        }

        return FromExtension(fileName) ?? "application/octet-stream";
    }

    public static string? SniffImage(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return "image/png";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return "image/jpeg";
        if (header.Length >= 6
            && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F')
            return "image/gif";
        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return "image/webp";
        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
            return "image/bmp";
        return null;
    }

    private static string? FromExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".txt" => "text/plain",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".zip" => "application/zip",
        _ => null,
    };
}
