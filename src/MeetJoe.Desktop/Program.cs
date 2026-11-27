using System.Threading;
using Avalonia;
using Avalonia.WebView.Desktop;
using MeetJoe.App.Controls;
using MeetJoe.App.Services;
using Velopack;

namespace MeetJoe.Desktop;

internal static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before UI startup so install/update/uninstall hooks can exit cleanly.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .Run();

        var deepLinkArg = args.FirstOrDefault(a =>
            a.StartsWith("meetjoe:", StringComparison.OrdinalIgnoreCase));

        // One UI process: toast "Show" / protocol activation forwards the link and exits.
        _instanceMutex = new Mutex(true, @"Local\MeetJoe.Desktop.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            if (!string.IsNullOrWhiteSpace(deepLinkArg))
                DeepLinkDrop.Write(deepLinkArg);
            return;
        }

        ApplyLaunchDeepLink(deepLinkArg, args);

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
            WindowsProtocolRegistration.EnsureRegistered();
            WebViewPlatformHooks.OnWebViewCreated = WindowsWebViewMediaAccess.Attach;
            MeetJoe.App.App.SessionStoreFactory = () => OperatingSystem.IsWindows() ? new WindowsDpapiSessionStore() : new NullSessionStore();
        }
        MeetJoe.App.App.UpdateServiceFactory = config => new VelopackAppUpdateService(config);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch { /* ignore */ }
            _instanceMutex.Dispose();
        }
    }

    private static void ApplyLaunchDeepLink(string? deepLinkArg, string[] args)
    {
        // meetjoe://join?token=… from email deep links.
        MeetJoe.App.App.PendingDeepLink = args
            .Select(DeepLinkParser.TryParseJoinToken)
            .FirstOrDefault(t => t is not null);

        // meetjoe://chat?id=… from mention notification Show.
        var chatId = DeepLinkParser.TryParseChatId(deepLinkArg)
                     ?? args.Select(DeepLinkParser.TryParseChatId).FirstOrDefault(id => id is not null);
        if (chatId is { } id)
            MeetJoe.App.App.PendingChatDeepLink = id;
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
