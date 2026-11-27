using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MeetJoe.App.Services;

/// <summary>Native OS notification banners (macOS Notification Center / Windows toast).</summary>
public interface IOsNotificationService
{
    /// <summary>Shows a system notification. Safe to call from any thread; failures are ignored.</summary>
    void Show(string title, string body);
}

/// <summary>No-op for headless tests.</summary>
public sealed class NullOsNotificationService : IOsNotificationService
{
    public void Show(string title, string body) { }
}

/// <summary>Records notifications for view-model tests.</summary>
public sealed class RecordingOsNotificationService : IOsNotificationService
{
    public List<(string Title, string Body)> Items { get; } = [];

    public void Show(string title, string body) => Items.Add((title, body));
}

/// <summary>
/// Posts OS-level alerts via <c>osascript</c> (macOS) or a Windows toast / balloon tip.
/// These appear outside MeetJoe — like Terminal/Cursor waiting alerts.
/// </summary>
public sealed class OsNotificationService : IOsNotificationService
{
    public void Show(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
        title = string.IsNullOrWhiteSpace(title) ? "MeetJoe" : title.Trim();
        body = (body ?? string.Empty).Trim();

        _ = Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    ShowMac(title, body);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    ShowWindows(title, body);
                else
                    ShowLinux(title, body);
            }
            catch
            {
                // Notifications are best-effort — never break the app shell.
            }
        });
    }

    private static void ShowMac(string title, string body)
    {
        // display notification appears in Notification Center (same family as the system Alert banner).
        var script =
            $"display notification {AppleScriptString(body)} with title {AppleScriptString(title)} " +
            $"subtitle {AppleScriptString("Scheduled meeting")} sound name \"Glass\"";
        Run("osascript", ["-e", script]);
    }

    private static void ShowWindows(string title, string body)
    {
        // Write a small script so quotes/newlines in the meeting title cannot break -Command.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"meetjoe-toast-{Guid.NewGuid():N}.ps1");
        var xmlTitle = XmlEscape(title);
        var xmlBody = XmlEscape(body);
        var psTitle = EscapePs(title);
        var psBody = EscapePs(body);
        var script =
            "$ErrorActionPreference = 'Stop'\n" +
            "try {\n" +
            "  [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null\n" +
            "  [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null\n" +
            "  $xml = New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
            "  $xml.LoadXml('<toast><visual><binding template=\"ToastGeneric\">" +
            $"<text>{xmlTitle}</text><text>{xmlBody}</text>" +
            "</binding></visual></toast>')\n" +
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

    private static string AppleScriptString(string value)
    {
        // AppleScript quoted string with backslash/quote escaping.
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
        return $"\"{escaped}\"";
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
