using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MeetJoe.App.Services;
using MeetJoe.App.ViewModels;
using MeetJoe.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace MeetJoe.App.Tests;

/// <summary>
/// Runs only when MEETJOE_LIVE_API points at a running MeetJoe server. Uses the same discoverer
/// as <see cref="AvaloniaFactAttribute"/> (which is sealed), so the test runs on the UI thread.
/// </summary>
[Xunit.Sdk.XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
public sealed class LiveFactAttribute : FactAttribute
{
    public const string Variable = "MEETJOE_LIVE_API";

    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} (for example http://localhost:5080) to run against a live server.";
    }
}

/// <summary>
/// Two people using the real client (sign-in, REST, SignalR) against a real server:
/// the MVP path of login, chat, channel, meeting and file sharing.
/// </summary>
public class LiveServerTests
{
    private const string Password = "correct-horse-42";

    [LiveFact]
    public async Task Two_people_can_chat_share_a_file_work_in_a_channel_and_meet()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var (anaApp, anaPlatform) = NewClient();
        var (benApp, benPlatform) = NewClient();

        // --- Accounts and organization
        var ana = await RegisterAsync(anaApp, $"ana-{run}@example.com", "Ana Reyes", s => s.OrganizationName = $"Northwind {run}", s => s.CreateCommand);
        Assert.True(ana.IsConnected);
        var code = ana.Settings.JoinCode;
        Assert.NotNull(code);

        var ben = await RegisterAsync(benApp, $"ben-{run}@example.com", "Ben Okafor", s => s.JoinCode = code.ToLowerInvariant(), s => s.JoinCommand);
        Assert.True(ben.IsConnected);

        // --- Presence: Ana learns that Ben joined and is online, without refreshing
        ana.Chat.BeginNewChatCommand.Execute(null);
        await Until(() =>
        {
            ana.Chat.BeginNewChatCommand.Execute(null);
            return ana.Chat.Candidates.Any(p => p.DisplayName == "Ben Okafor" && p.Status == PresenceStatus.Available);
        }, "Ana sees Ben online");

        // --- Direct chat over the hub
        ana.Chat.Candidates.Single(p => p.DisplayName == "Ben Okafor").IsSelected = true;
        await ana.Chat.StartChatCommand.ExecuteAsync(null);
        Assert.Null(ana.Chat.NewChatError);
        var anaThread = ana.Chat.Thread!;
        anaThread.Draft = "Hi Ben — is the build ready?";
        await anaThread.SendCommand.ExecuteAsync(null);
        Assert.Null(anaThread.ErrorMessage);

        await Until(() => ben.Chat.Conversations.Any(c => c.UnreadCount == 1), "Ben sees an unread chat");
        Assert.Equal(1, ben.RailItems.Single(i => i.Label == "Chat").Badge);
        var benConversation = ben.Chat.Conversations.Single();
        Assert.Equal("Ana Reyes", benConversation.Title);
        Assert.Equal("Hi Ben — is the build ready?", benConversation.Preview);

        ben.Chat.Selected = benConversation;
        await Until(() => ben.Chat.Thread?.Messages.Count == 1 && ben.Chat.UnreadTotal == 0, "Ben reads the message");

        // --- A reply with a file attached
        var payload = Encoding.UTF8.GetBytes($"release notes {run}");
        benPlatform.NextPick = new PickedFile("release-notes.txt", () => Task.FromResult<Stream>(new MemoryStream(payload)));
        var benThread = ben.Chat.Thread!;
        await benThread.AttachCommand.ExecuteAsync(null);
        Assert.Null(benThread.ErrorMessage);
        benThread.Draft = "Yes, notes attached.";
        await benThread.SendCommand.ExecuteAsync(null);
        Assert.Null(benThread.ErrorMessage);

        await Until(() => anaThread.Messages.Count == 2, "Ana sees the reply");
        var attachment = Assert.Single(anaThread.Messages[1].Attachments);
        Assert.Equal("release-notes.txt", attachment.FileName);
        await attachment.DownloadCommand.ExecuteAsync(null);
        Assert.Equal("Saved", attachment.Status);
        Assert.Equal(payload, anaPlatform.LastSaved!.ToArray());
        Assert.Equal(0, ana.Chat.UnreadTotal);

        await ana.Files.LoadAsync();
        Assert.Equal("release-notes.txt", Assert.Single(ana.Files.Files).FileName);

        // --- Workspace and channel
        ana.Workspaces.BeginCreateWorkspaceCommand.Execute(null);
        ana.Workspaces.NewWorkspaceName = "Product";
        await ana.Workspaces.CreateWorkspaceCommand.ExecuteAsync(null);
        Assert.Null(ana.Workspaces.CreateError);
        Assert.Equal("General", ana.Workspaces.SelectedChannel?.Name);
        Assert.Empty(ben.Workspaces.Workspaces);

        await ana.Workspaces.ToggleMembersPanelCommand.ExecuteAsync(null);
        await ana.Workspaces.AddMemberCommand.ExecuteAsync(ana.Workspaces.Addable.Single(p => p.DisplayName == "Ben Okafor"));
        Assert.Null(ana.Workspaces.MembersError);
        await Until(() => ben.Workspaces.Workspaces.Count == 1, "Ben is added to the workspace");

        ben.Workspaces.SelectChannelCommand.Execute(ben.Workspaces.Workspaces[0].Channels[0]);
        var post = ana.Workspaces.Thread!;
        post.Draft = "Welcome to Product.";
        await post.SendCommand.ExecuteAsync(null);
        await Until(() => ben.Workspaces.Thread?.Messages.Any(m => m.Body == "Welcome to Product.") == true, "Ben sees the channel post");

        // --- Meeting
        ana.Calendar.NewMeetingTitle = "Release check";
        await ana.Calendar.MeetNowCommand.ExecuteAsync(null);
        Assert.Null(ana.Calendar.ErrorMessage);
        Assert.StartsWith("ws", ana.Calendar.Stage!.MediaServer);

        await Until(() => ben.Calendar.Meetings.Any(m => m.Title == "Release check"), "Ben sees the meeting");
        await ben.Calendar.JoinCommand.ExecuteAsync(ben.Calendar.Meetings.Single());
        Assert.True(ben.Calendar.IsInMeeting);
        await Until(() => ana.Calendar.Stage?.Meeting.Participants.Count == 2, "Ana sees Ben join");

        await ana.Calendar.EndCommand.ExecuteAsync(null);
        await Until(() => !ben.Calendar.IsInMeeting, "Ben's stage closes when the host ends the meeting");

        // --- Presence changes and sign-out
        var benAsSeenByAna = ana.Chat.Conversations.Single().Other!;
        ben.Settings.Availability = ben.Settings.AvailabilityOptions.Single(o => o.Status == PresenceStatus.Busy);
        await Until(() => benAsSeenByAna.Status == PresenceStatus.Busy, "Ana sees Ben busy");

        await benApp.SignOutAsync();
        await Until(() => benAsSeenByAna.Status == PresenceStatus.Offline, "Ana sees Ben offline");

        await anaApp.SignOutAsync();
    }

    [LiveFact]
    public async Task A_wrong_password_is_rejected_with_a_readable_message()
    {
        var (app, _) = NewClient();
        var login = Assert.IsType<LoginViewModel>(app.Current);
        login.Email = $"nobody-{Guid.NewGuid():N}@example.com";
        login.Password = "definitely-wrong";

        await login.SubmitCommand.ExecuteAsync(null);

        Assert.Same(login, app.Current);
        Assert.False(string.IsNullOrWhiteSpace(login.ErrorMessage));
        Assert.DoesNotContain("Something went wrong", login.ErrorMessage);
    }

    private static (MainViewModel App, FakePlatform Platform) NewClient()
    {
        var config = new AppConfig { ApiBaseUri = new Uri(Environment.GetEnvironmentVariable(LiveFactAttribute.Variable)!) };
        var platform = new FakePlatform();
        var services = App.BuildServices(config, platform);
        return (services.GetRequiredService<MainViewModel>(), platform);
    }

    private static async Task<ShellViewModel> RegisterAsync(
        MainViewModel app,
        string email,
        string name,
        Action<OrganizationSetupViewModel> fill,
        Func<OrganizationSetupViewModel, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand> submit)
    {
        var login = Assert.IsType<LoginViewModel>(app.Current);
        login.ToggleModeCommand.Execute(null);
        login.Email = email;
        login.Password = Password;
        login.DisplayName = name;
        // The live API rate-limits /connect/token; brief waits if a previous suite run filled the window.
        for (var attempt = 0; ; attempt++)
        {
            await login.SubmitCommand.ExecuteAsync(null);
            if (login.ErrorMessage is null ||
                !login.ErrorMessage.Contains("Too many attempts", StringComparison.Ordinal) ||
                attempt == 3)
                break;
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
        Assert.Null(login.ErrorMessage);

        var setup = Assert.IsType<OrganizationSetupViewModel>(app.Current);
        fill(setup);
        await submit(setup).ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);
        return Assert.IsType<ShellViewModel>(app.Current);
    }

    /// <summary>Waits for something delivered by the server, letting UI-thread events run meanwhile.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for: {what}");
    }
}
