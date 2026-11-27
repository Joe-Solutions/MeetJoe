using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MeetJoe.Desktop;

/// <summary>
/// Registers meetjoe:// under HKCU so Windows toast Show / protocol activation
/// can launch (or hand off to) the running MeetJoe process.
/// </summary>
internal static class WindowsProtocolRegistration
{
    private const string Scheme = "meetjoe";

    public static void EnsureRegistered()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

            var command = $"\"{exe}\" \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            if (key is null) return;

            key.SetValue(null, "URL:MeetJoe");
            key.SetValue("URL Protocol", "");

            using var icon = key.CreateSubKey("DefaultIcon");
            icon?.SetValue(null, $"\"{exe}\",0");

            using var commandKey = key.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue(null, command);
        }
        catch
        {
            // Protocol registration is best-effort (toast Show still falls back to Activate).
            Debug.WriteLine("MeetJoe: failed to register meetjoe:// protocol.");
        }
    }
}
