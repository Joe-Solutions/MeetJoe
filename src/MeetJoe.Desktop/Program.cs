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
        WebViewPlatformHooks.OnWebViewCreated = MacOsWebViewMediaAccess.Attach;
        WebViewPlatformHooks.RequestOsMediaPermissionAsync = MacOsMediaPermissions.EnsureAsync;
#endif
        MeetJoe.App.App.UpdateServiceFactory = config => new VelopackAppUpdateService(config);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MeetJoe.App.App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseDesktopWebView();
}
