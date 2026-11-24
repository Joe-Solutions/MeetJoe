using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeetJoe.App.Controls;
using MeetJoe.App.Tests;
using MeetJoe.App.ViewModels;
using MeetJoe.App.Views;

[assembly: AvaloniaTestApplication(typeof(TestApp))]
// Avalonia Headless shares one UI session for the assembly; parallel facts race on Dispatcher.PushFrame.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MeetJoe.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Screens are captured as soon as they are shown, so they must not be mid-transition.
        SlideFadeTransition.Enabled = false;
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>
/// Loads every screen through the real views, templates and styles. A missing resource, a broken
/// binding path or a bad template fails here. Each screen is also written to artifacts/screenshots
/// so the design can be reviewed without launching the app.
/// </summary>
public class ScreenTests
{
    private readonly World _world = new();

    [AvaloniaFact]
    public void Sign_in()
    {
        var app = _world.NewApp();
        Capture(app, "01-sign-in");
    }

    [AvaloniaFact]
    public void Create_account_with_an_error()
    {
        var app = _world.NewApp();
        var login = (LoginViewModel)app.Current;
        login.ToggleModeCommand.Execute(null);
        login.DisplayName = "Ana Reyes";
        login.Email = "ana@northwind.example";
        login.ErrorMessage = "An account with this email already exists.";
        Capture(app, "02-create-account");
    }

    [AvaloniaFact]
    public async Task Organization_setup()
    {
        _world.Api.Organizations.Clear();
        var app = _world.NewApp();
        await app.EnterAsync();
        Capture(app, "03-organization-setup");
    }

    [AvaloniaFact]
    public async Task Chat_with_nothing_open()
    {
        var (app, _) = await ShellAsync();
        Capture(app, "04-chat-empty");
    }

    [AvaloniaFact]
    public async Task Chat_conversation_with_people_panel()
    {
        var (app, shell) = await ShellAsync();
        shell.Chat.Selected = shell.Chat.Conversations[0];
        shell.Chat.ToggleMembersPanelCommand.Execute(null);
        shell.Chat.Thread!.Draft = "Uploading it now";
        var window = Capture(app, "05-chat-conversation");

        Assert.Equal(5, window.GetVisualDescendantsOfType<SelectableTextBlock>().Count(t => t.IsVisible));
    }

    [AvaloniaFact]
    public async Task New_chat()
    {
        var (app, shell) = await ShellAsync();
        shell.Chat.BeginNewChatCommand.Execute(null);
        shell.Chat.Candidates[0].IsSelected = true;
        Capture(app, "06-new-chat");
    }

    [AvaloniaFact]
    public async Task Workspace_channel_with_members_panel()
    {
        var (app, shell) = await ShellAsync();
        shell.NavigateCommand.Execute(shell.RailItems[2]);
        shell.Workspaces.SelectChannelCommand.Execute(shell.Workspaces.Workspaces[0].Channels[0]);
        await shell.Workspaces.ToggleMembersPanelCommand.ExecuteAsync(null);
        shell.Workspaces.BeginAddChannelCommand.Execute(shell.Workspaces.Workspaces[1]);
        Capture(app, "07-workspace-channel");
    }

    [AvaloniaFact]
    public async Task New_workspace()
    {
        var (app, shell) = await ShellAsync();
        shell.NavigateCommand.Execute(shell.RailItems[2]);
        shell.Workspaces.BeginCreateWorkspaceCommand.Execute(null);
        Capture(app, "08-new-workspace");
    }

    [AvaloniaFact]
    public async Task Calendar()
    {
        var (app, shell) = await ShellAsync();
        shell.NavigateCommand.Execute(shell.RailItems[3]);
        Capture(app, "09-calendar");
    }

    [AvaloniaFact]
    public async Task Calls()
    {
        var (app, shell) = await ShellAsync();
        shell.NavigateCommand.Execute(shell.RailItems[4]);
        Assert.IsType<CallsViewModel>(shell.Current);
        Capture(app, "09b-calls");
    }

    [AvaloniaFact]
    public async Task Meeting_stage()
    {
        var (app, shell) = await ShellAsync();
        shell.NavigateCommand.Execute(shell.RailItems[3]);
        await shell.Calendar.JoinCommand.ExecuteAsync(shell.Calendar.Meetings[0]);
        Capture(app, "10-meeting-stage");
    }

    [AvaloniaFact]
    public async Task In_a_meeting_while_offline_on_another_section()
    {
        var (app, shell) = await ShellAsync();
        await shell.Calendar.JoinCommand.ExecuteAsync(shell.Calendar.Meetings[0]);
        shell.NavigateCommand.Execute(shell.RailItems[5]);
        _world.Realtime.SetConnected(false);
        Capture(app, "11-files-with-banners");
    }

    [AvaloniaFact]
    public async Task Settings()
    {
        var (app, shell) = await ShellAsync();
        shell.NavigateCommand.Execute(shell.SettingsItem);
        Capture(app, "12-settings");
    }

    [AvaloniaFact]
    public async Task Activity_feed()
    {
        var (app, shell) = await ShellAsync();
        shell.NavigateCommand.Execute(shell.RailItems[0]);
        Assert.IsType<ActivityViewModel>(shell.Current);
        Capture(app, "13-activity");
    }

    [AvaloniaFact]
    public async Task Smallest_supported_window()
    {
        var (app, shell) = await ShellAsync();
        shell.Chat.Selected = shell.Chat.Conversations[0];
        shell.Chat.ToggleMembersPanelCommand.Execute(null);
        Capture(app, "14-chat-minimum-size", 960, 600);
    }

    private async Task<(MainViewModel App, ShellViewModel Shell)> ShellAsync()
    {
        var app = _world.NewApp();
        await app.EnterAsync();
        return (app, Assert.IsType<ShellViewModel>(app.Current));
    }

    private static Window Capture(MainViewModel app, string name, int width = 1280, int height = 800)
    {
        var window = new MainWindow { DataContext = app, Width = width, Height = height };
        window.Show();

        // Let bindings, layout and the entrance animations settle before taking the frame.
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(width, frame.PixelSize.Width);

        var directory = Path.Combine(RepositoryRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name + ".png"));
        return window;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeetJoe.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("MeetJoe.sln not found above the test output.");
    }
}

internal static class VisualSearch
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Visual root) where T : Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
