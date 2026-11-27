using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using MeetJoe.App.Services;
using MeetJoe.App.ViewModels;
using MeetJoe.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MeetJoe.App;

public partial class App : Application
{
    /// <summary>Desktop host plugs Velopack (or another installer) in before the UI starts.</summary>
    public static Func<AppConfig, IAppUpdateService>? UpdateServiceFactory { get; set; }

    /// <summary>Desktop host plugs in the OS credential store so sign-in survives restarts.</summary>
    public static Func<ISessionStore>? SessionStoreFactory { get; set; }

    /// <summary>Invite token from a meetjoe://join?token=… deep link, if the process was launched that way.</summary>
    public static string? PendingDeepLink { get; set; }

    /// <summary>Conversation to open from meetjoe://chat?id=… (mention Show / protocol activation).</summary>
    public static Guid? PendingChatDeepLink { get; set; }

    /// <summary>Temp file used when a second process (toast Show) forwards a deep link to the running app.</summary>
    public static string DeepLinkDropPath { get; } =
        Path.Combine(Path.GetTempPath(), "meetjoe-pending-deeplink.txt");

    public override void RegisterServices()
    {
        base.RegisterServices();
        AvaloniaWebViewBuilder.Initialize(default);
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var platform = new AvaloniaPlatformServices();
        var services = BuildServices(AppConfig.FromEnvironment(), platform);
        var main = services.GetRequiredService<MainViewModel>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                var window = new MainWindow { DataContext = main };
                platform.TopLevelAccessor = () => window;
                desktop.MainWindow = window;
                desktop.Exit += (_, _) =>
                {
                    // RealtimeClient is IAsyncDisposable-only; sync Dispose() aborts on exit.
                    if (services is IAsyncDisposable asyncDisposable)
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    else
                        services.Dispose();
                };
                break;

            // Mobile hosts (Phase 7) show the same root view without a window.
            case ISingleViewApplicationLifetime singleView:
                var view = new MainView { DataContext = main };
                platform.TopLevelAccessor = () => TopLevel.GetTopLevel(view);
                singleView.MainView = view;
                break;
        }

        base.OnFrameworkInitializationCompleted();
        _ = main.ResumeSessionAsync();
        _ = main.StartUpdateCheckAsync();
    }

    internal static ServiceProvider BuildServices(AppConfig config, IPlatformServices platform)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(platform);
        services.AddSingleton<ICallTonePlayer, CallTonePlayer>();
        services.AddSingleton<IOsNotificationService, OsNotificationService>();
        services.AddSingleton<IAppUpdateService>(sp =>
            UpdateServiceFactory?.Invoke(sp.GetRequiredService<AppConfig>())
            ?? new NullAppUpdateService());

        services.AddSingleton<IAuthService>(_ => new AuthService(
            new HttpClient { BaseAddress = config.ApiBaseUri, Timeout = TimeSpan.FromSeconds(30) },
            SessionStoreFactory?.Invoke()));

        services.AddSingleton<IMeetJoeApi>(provider => new MeetJoeApi(
            new HttpClient(new AuthHeaderHandler(provider.GetRequiredService<IAuthService>())
            {
                InnerHandler = new SocketsHttpHandler(),
            })
            {
                BaseAddress = config.ApiBaseUri,
                // Long enough for a recording upload (up to ~200 MB) on a slow connection.
                Timeout = TimeSpan.FromMinutes(15),
            },
            provider.GetRequiredService<IAuthService>()));

        services.AddSingleton<IRealtimeClient, RealtimeClient>();
        services.AddSingleton<AvatarCache>();
        services.AddSingleton<MainViewModel>();
        var provider = services.BuildServiceProvider();
        AvatarImages.Current = provider.GetRequiredService<AvatarCache>();
        return provider;
    }
}
