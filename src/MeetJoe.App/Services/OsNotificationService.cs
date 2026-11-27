using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MeetJoe.App.Services;

/// <summary>Native OS notification banners (macOS Notification Center / Windows toast).</summary>
public interface IOsNotificationService
{
    /// <summary>Shows a system notification. Safe to call from any thread; failures are ignored.</summary>
    void Show(string title, string body);

    /// <summary>
    /// Shows a system notification with an optional action (e.g. Show → open a chat).
    /// <paramref name="actionUri"/> should be a meetjoe:// deep link when provided.
    /// </summary>
    void Show(string title, string body, Uri? actionUri, string actionLabel = "Show");
}

/// <summary>No-op for headless tests.</summary>
public sealed class NullOsNotificationService : IOsNotificationService
{
    public void Show(string title, string body) { }
    public void Show(string title, string body, Uri? actionUri, string actionLabel = "Show") { }
}

/// <summary>Records notifications for view-model tests.</summary>
public sealed class RecordingOsNotificationService : IOsNotificationService
{
    public List<(string Title, string Body, Uri? ActionUri, string? ActionLabel)> Items { get; } = [];

    public void Show(string title, string body) =>
        Show(title, body, null, "Show");

    public void Show(string title, string body, Uri? actionUri, string actionLabel = "Show") =>
        Items.Add((title, body, actionUri, actionLabel));
}

/// <summary>
/// Posts OS-level alerts via Notification Center (macOS) or a Windows toast / balloon tip.
/// </summary>
public sealed class OsNotificationService : IOsNotificationService
{
    public void Show(string title, string body) =>
        Show(title, body, null, "Show");

    public void Show(string title, string body, Uri? actionUri, string actionLabel = "Show")
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
        title = string.IsNullOrWhiteSpace(title) ? "MeetJoe" : title.Trim();
        body = (body ?? string.Empty).Trim();
        actionLabel = string.IsNullOrWhiteSpace(actionLabel) ? "Show" : actionLabel.Trim();

        _ = Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    ShowMac(title, body, actionUri);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    ShowWindows(title, body, actionUri, actionLabel);
                else
                    ShowLinux(title, body);
            }
            catch
            {
                // Notifications are best-effort — never break the app shell.
            }
        });
    }

    private static void ShowMac(string title, string body, Uri? actionUri)
    {
        // Must post as MeetJoe.app — bare osascript banners belong to Script Editor,
        // so Show/click opens Script Editor instead of MeetJoe + the group chat.
        if (OsNotificationHooks.ShowMac is { } show)
        {
            show(title, body, actionUri);
            return;
        }

        // Headless / tests without a Desktop host: no Script Editor side effects.
    }

    private static void ShowWindows(string title, string body, Uri? actionUri, string actionLabel)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"meetjoe-toast-{Guid.NewGuid():N}.ps1");
        var xmlTitle = XmlEscape(title);
        var xmlBody = XmlEscape(body);
        var xmlAction = XmlEscape(actionLabel);
        var psTitle = EscapePs(title);
        var psBody = EscapePs(body);
        var launchAttr = actionUri is null
            ? ""
            : $" launch=\"{XmlEscape(actionUri.AbsoluteUri)}\"";
        var actionXml = actionUri is null
            ? ""
            : "<actions>"
              + $"<action content=\"{xmlAction}\" arguments=\"{XmlEscape(actionUri.AbsoluteUri)}\" activationType=\"protocol\"/>"
              + "</actions>";
        var script =
            "$ErrorActionPreference = 'Stop'\n" +
            "try {\n" +
            "  [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null\n" +
            "  [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null\n" +
            "  $xml = New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
            "  $xml.LoadXml('<toast" + launchAttr + "><visual><binding template=\"ToastGeneric\">" +
            $"<text>{xmlTitle}</text><text>{xmlBody}</text>" +
            "</binding></visual>" + actionXml + "</toast>')\n" +
            "  $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)\n" +
            "  [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('MeetJoe').Show($toast)\n" +
            "} catch {\n" +
            "  Add-Type -AssemblyName System.Windows.Forms\n" +
            "  Add-Type -AssemblyName System.Drawing\n" +
            "  $ni = New-Object System.Windows.Forms.NotifyIcon\n" +
            "  $ni.Icon = [System.Drawing.SystemIcons]::Information\n" +
            "  $ni.Visible = $true\n" +
            $"  $ni.BalloonTipTitle = '{psTitle}'\n" +
            $"  $ni.BalloonTipText = '{psBody}'\n" +
            "  $ni.ShowBalloonTip(10000)\n" +
            "  Start-Sleep -Seconds 12\n" +
            "  $ni.Dispose()\n" +
            "}\n";
        try
        {
            File.WriteAllText(scriptPath, script, Encoding.UTF8);
            Run("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath]);
        }
        finally
        {
            try { File.Delete(scriptPath); }
            catch { /* temp cleanup */ }
        }
    }

    private static void ShowLinux(string title, string body)
    {
        if (TryRun("notify-send", ["--app-name=MeetJoe", title, body])) return;
        TryRun("zenity", ["--notification", $"--text={title}\n{body}"]);
    }

    private static string EscapePs(string value) =>
        value.Replace("'", "''").Replace("\r", " ").Replace("\n", " ");

    private static string XmlEscape(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    private static void Run(string fileName, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start);
        process?.WaitForExit(15_000);
    }

    private static bool TryRun(string fileName, IReadOnlyList<string> args)
    {
        try
        {
            Run(fileName, args);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
