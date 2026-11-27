namespace MeetJoe.Desktop;

/// <summary>
/// Hands a deep link from a second process (toast Show / protocol activation) to the running app.
/// </summary>
internal static class DeepLinkDrop
{
    public static void Write(string uri)
    {
        try { File.WriteAllText(MeetJoe.App.App.DeepLinkDropPath, uri.Trim()); }
        catch { /* best-effort */ }
    }
}
