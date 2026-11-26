using Avalonia;
using Avalonia.WebView.Desktop;
using MeetJoe.App.Controls;
using MeetJoe.App.Services;
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

        // meetjoe://join?token=… from email deep links.
        MeetJoe.App.App.PendingDeepLink = args
            .Select(DeepLinkParser.TryParseJoinToken)
            .FirstOrDefault(t => t is not null);

#if OSX
        if (OperatingSystem.IsMacOS())
        {
            WebViewPlatformHooks.CreateMediaWebView = () => new MacMediaWebView();
            WebViewPlatformHooks.RequestOsMediaPermissionAsync = MacOsMediaPermissions.EnsureAsync;
            MeetJoe.App.App.SessionStoreFactory = () => new MacKeychainSessionStore();
        }
#endif
        if (OperatingSystem.IsWindows())
        {
            WebViewPlatformHooks.OnWebViewCreated = WindowsWebViewMediaAccess.Attach;
            MeetJoe.App.App.SessionStoreFactory = () => OperatingSystem.IsWindows() ? new WindowsDpapiSessionStore() : new NullSessionStore();
        }
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
