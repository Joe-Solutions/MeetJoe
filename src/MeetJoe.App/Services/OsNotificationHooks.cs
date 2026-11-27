namespace MeetJoe.App.Services;

/// <summary>
/// Desktop hosts plug in a real macOS Notification Center poster (MeetJoe.app),
/// so clicks open MeetJoe instead of Script Editor (osascript).
/// </summary>
public static class OsNotificationHooks
{
    /// <summary>Posts a Notification Center banner as MeetJoe. Null keeps the osascript fallback.</summary>
    public static Action<string, string, Uri?>? ShowMac { get; set; }
}
