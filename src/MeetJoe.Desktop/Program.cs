using Avalonia;
using Avalonia.WebView.Desktop;

namespace MeetJoe.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MeetJoe.App.App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseDesktopWebView();
}
