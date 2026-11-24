using Avalonia;
using Avalonia.WebView.Desktop;
using MeetJoe.App.Controls;
using Velopack;

namespace MeetJoe.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before UI startup so install/update/uninstall hooks can exit cleanly.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .Run();

#if OSX
        if (OperatingSystem.IsMacOS())
        {
            WebViewPlatformHooks.CreateMediaWebView = () => new MacMediaWebView();
            WebViewPlatformHooks.RequestOsMediaPermissionAsync = MacOsMediaPermissions.EnsureAsync;
        }
#endif
        if (OperatingSystem.IsWindows())
            WebViewPlatformHooks.OnWebViewCreated = WindowsWebViewMediaAccess.Attach;
        MeetJoe.App.App.UpdateServiceFactory = config => new VelopackAppUpdateService(config);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<MeetJoe.App.App>()
            .UsePlatformDetect()
            .LogToTrace();
        // macOS hosts WKWebView itself (MacMediaWebView); WebView.Avalonia is Windows-only here.
        return OperatingSystem.IsMacOS() ? builder : builder.UseDesktopWebView();
    }
}
