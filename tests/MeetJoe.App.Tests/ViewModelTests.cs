using MeetJoe.App.Controls;
using MeetJoe.App.Services;
using MeetJoe.App.ViewModels;
using MeetJoe.Contracts;

namespace MeetJoe.App.Tests;

public class ChatViewModelTests
{
    private readonly World _world = new();

    private async Task<ChatViewModel> LoadedChatAsync()
    {
        var chat = new ChatViewModel(_world.NewSession());
        await chat.LoadAsync();
        return chat;
    }

    private static ConversationViewModel Find(ChatViewModel chat, ConversationDto dto) =>
        chat.Conversations.Single(c => c.Id == dto.Id);

    [Fact]
    public async Task Conversations_are_listed_with_titles_taken_from_the_other_people()
    {
        var chat = await LoadedChatAsync();

        Assert.Equal(["Ben Okafor", "Launch planning", "Priya Nair"], chat.Conversations.Select(c => c.Title));
        Assert.Equal(2, chat.UnreadTotal);
    }

    [Fact]
    public async Task Opening_a_conversation_shows_its_messages_and_marks_it_read()
    {
        var chat = await LoadedChatAsync();

        chat.Selected = Find(chat, _world.LaunchGroup);

        Assert.NotNull(chat.Thread);
        Assert.Equal(0, chat.UnreadTotal);
        Assert.Contains(_world.LaunchGroup.Id, _world.Api.MarkedRead);
    }

    [Fact]
    public async Task A_message_for_the_open_conversation_appears_and_is_not_counted_as_unread()
    {
        var chat = await LoadedChatAsync();
        chat.Selected = Find(chat, _world.WithBen);
        var before = chat.Thread!.Messages.Count;

        _world.Realtime.Receive(_world.Api.NewMessage(_world.WithBen.Id, null, _world.Ben, "On my way."));

        Assert.Equal(before + 1, chat.Thread.Messages.Count);
        Assert.Equal("On my way.", chat.Thread.Messages[^1].Body);
        Assert.Equal(0, Find(chat, _world.WithBen).UnreadCount);
    }

    [Fact]
    public async Task A_message_for_another_conversation_is_counted_and_moves_it_to_the_top()
    {
        var chat = await LoadedChatAsync();
        chat.Selected = Find(chat, _world.WithBen);

        _world.Realtime.Receive(_world.Api.NewMessage(_world.WithPriya.Id, null, _world.Priya, "Got a minute?"));

        var priya = Find(chat, _world.WithPriya);
        Assert.Same(priya, chat.Conversations[0]);
        Assert.Equal(1, priya.UnreadCount);
        Assert.Equal("Got a minute?", priya.Preview);
        Assert.Same(Find(chat, _world.WithBen), chat.Selected);
    }

    [Fact]
    public async Task My_own_message_from_another_device_is_not_counted_as_unread()
    {
        var chat = await LoadedChatAsync();

        _world.Realtime.Receive(_world.Api.NewMessage(_world.WithPriya.Id, null, _world.Ana, "Sent from my phone"));

        Assert.Equal(0, Find(chat, _world.WithPriya).UnreadCount);
    }

    [Fact]
    public async Task Refreshing_the_list_keeps_the_open_conversation_and_its_thread()
    {
        var chat = await LoadedChatAsync();
        chat.Selected = Find(chat, _world.WithBen);
        var thread = chat.Thread;

        _world.Realtime.RaiseConversationsChanged();

        Assert.Same(thread, chat.Thread);
        Assert.Equal(_world.WithBen.Id, chat.Selected?.Id);
        Assert.True(chat.Selected!.IsSelected);
    }

    [Fact]
    public async Task Sending_clears_the_draft_and_shows_the_message_once_even_when_the_server_echoes_it()
    {
        var chat = await LoadedChatAsync();
        chat.Selected = Find(chat, _world.WithBen);
        var thread = chat.Thread!;
        var before = thread.Messages.Count;

        thread.Draft = "Build is uploaded.";
        await thread.SendCommand.ExecuteAsync(null);
        _world.Realtime.Receive(_world.Api.Messages[_world.WithBen.Id][^1]);

        Assert.Equal(string.Empty, thread.Draft);
        Assert.Equal(before + 1, thread.Messages.Count);
    }

    [Fact]
    public async Task A_failed_send_puts_the_draft_back_and_explains()
    {
        var chat = await LoadedChatAsync();
        chat.Selected = Find(chat, _world.WithBen);
        var thread = chat.Thread!;
        _world.Api.SendFailure = new HttpRequestException("connection refused");

        thread.Draft = "Build is uploaded.";
        await thread.SendCommand.ExecuteAsync(null);

        Assert.Equal("Build is uploaded.", thread.Draft);
        Assert.Contains("Can't reach the MeetJoe server", thread.ErrorMessage);
    }

    [Fact]
    public async Task Nothing_can_be_sent_from_an_empty_composer()
    {
        var chat = await LoadedChatAsync();
        chat.Selected = Find(chat, _world.WithBen);

        chat.Thread!.Draft = "   ";

        Assert.False(chat.Thread.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Consecutive_messages_from_one_person_are_grouped()
    {
        var chat = await LoadedChatAsync();
        chat.Selected = Find(chat, _world.WithBen);

        // Ben, Ana, Ana (same minute), Ben, Ana
        Assert.Equal([true, true, false, true, true], chat.Thread!.Messages.Select(m => m.StartsGroup));
    }

    [Fact]
    public async Task Starting_a_chat_needs_at_least_one_person()
    {
        var chat = await LoadedChatAsync();
        chat.BeginNewChatCommand.Execute(null);

        await chat.StartChatCommand.ExecuteAsync(null);

        Assert.Equal("Choose at least one person.", chat.NewChatError);
        Assert.True(chat.IsStartingChat);
    }

    [Fact]
    public async Task Starting_a_chat_opens_the_new_conversation()
    {
        var chat = await LoadedChatAsync();
        chat.BeginNewChatCommand.Execute(null);
        Assert.DoesNotContain(chat.Candidates, p => p.IsMe);

        chat.Candidates.Single(p => p.Id == _world.David.Id).IsSelected = true;
        await chat.StartChatCommand.ExecuteAsync(null);

        Assert.False(chat.IsStartingChat);
        Assert.Equal("David Kim", chat.Selected?.Title);
        Assert.NotNull(chat.Thread);
    }
}

public class WorkspacesViewModelTests
{
    private readonly World _world = new();

    private async Task<WorkspacesViewModel> LoadedAsync()
    {
        var workspaces = new WorkspacesViewModel(_world.NewSession());
        await workspaces.LoadAsync();
        return workspaces;
    }

    [Fact]
    public async Task Opening_a_channel_shows_its_posts()
    {
        var workspaces = await LoadedAsync();

        workspaces.SelectChannelCommand.Execute(workspaces.Workspaces[0].Channels[0]);

        Assert.Equal(3, workspaces.Thread!.Messages.Count);
        Assert.True(workspaces.Thread.IsChannel);
        Assert.Single(workspaces.Thread.Messages[0].Attachments);
    }

    [Fact]
    public async Task A_refresh_keeps_the_open_channel_and_what_the_user_was_reading()
    {
        var workspaces = await LoadedAsync();
        workspaces.SelectChannelCommand.Execute(workspaces.Workspaces[0].Channels[0]);
        var thread = workspaces.Thread;

        _world.Realtime.RaiseWorkspacesChanged();

        Assert.Same(thread, workspaces.Thread);
        Assert.True(workspaces.SelectedChannel!.IsSelected);
        Assert.Same(workspaces.Workspaces[0].Channels[0], workspaces.SelectedChannel);
    }

    [Fact]
    public async Task Posts_for_other_channels_do_not_leak_into_the_open_one()
    {
        var workspaces = await LoadedAsync();
        workspaces.SelectChannelCommand.Execute(workspaces.Workspaces[0].Channels[0]);
        var roadmap = _world.Product.Channels[1];

        _world.Realtime.Receive(_world.Api.NewMessage(null, roadmap.Id, _world.Ben, "Q3 themes are up."));

        Assert.Equal(3, workspaces.Thread!.Messages.Count);
    }

    [Fact]
    public async Task Adding_a_channel_opens_it()
    {
        var workspaces = await LoadedAsync();
        var product = workspaces.Workspaces[0];
        workspaces.BeginAddChannelCommand.Execute(product);
        product.NewChannelName = "Pilot feedback";

        await workspaces.AddChannelCommand.ExecuteAsync(product);

        Assert.Equal("Pilot feedback", workspaces.SelectedChannel?.Name);
    }

    [Fact]
    public async Task A_duplicate_channel_name_is_reported_next_to_the_field()
    {
        var workspaces = await LoadedAsync();
        var product = workspaces.Workspaces[0];
        workspaces.BeginAddChannelCommand.Execute(product);
        product.NewChannelName = "roadmap";

        await workspaces.AddChannelCommand.ExecuteAsync(product);

        Assert.Contains("already has a channel", product.ChannelError);
        Assert.True(product.IsAddingChannel);
    }

    [Fact]
    public async Task Owners_can_add_people_who_are_not_yet_members()
    {
        var workspaces = await LoadedAsync();
        workspaces.SelectChannelCommand.Execute(workspaces.Workspaces[0].Channels[0]);
        await workspaces.ToggleMembersPanelCommand.ExecuteAsync(null);

        Assert.True(workspaces.CanAddMembers);
        var david = Assert.Single(workspaces.Addable);
        await workspaces.AddMemberCommand.ExecuteAsync(david);

        Assert.Empty(workspaces.Addable);
        Assert.Contains(workspaces.Members, m => m.Person.Id == _world.David.Id);
    }

    [Fact]
    public async Task Members_of_a_workspace_they_do_not_own_cannot_add_people()
    {
        var workspaces = await LoadedAsync();

        workspaces.SelectChannelCommand.Execute(workspaces.Workspaces[1].Channels[0]);

        Assert.False(workspaces.CanAddMembers);
    }
}

public class CalendarViewModelTests
{
    private readonly World _world = new();

    [Fact]
    public async Task Meet_now_starts_a_meeting_and_puts_the_user_on_stage()
    {
        var calendar = new CalendarViewModel(_world.NewSession());
        await calendar.LoadAsync();

        await calendar.MeetNowCommand.ExecuteAsync(null);

        Assert.True(calendar.IsInMeeting);
        Assert.Equal("Ana Reyes's meeting", calendar.Stage!.Meeting.Title);
        Assert.Contains(calendar.Stage.Meeting.Participants, p => p.IsMe);
        Assert.True(calendar.Stage.Meeting.IsHostedByMe);
        Assert.True(calendar.Stage.IsMediaOpen);
        Assert.Contains("/meet/", calendar.Stage.MediaPageUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("token", calendar.Stage.MediaPageUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Joining_someone_elses_meeting_does_not_offer_to_end_it()
    {
        var calendar = new CalendarViewModel(_world.NewSession());
        await calendar.LoadAsync();

        await calendar.JoinCommand.ExecuteAsync(calendar.Meetings[0]);

        Assert.True(calendar.IsInMeeting);
        Assert.False(calendar.Stage!.Meeting.IsHostedByMe);
        Assert.Equal(3, calendar.Stage.Meeting.Participants.Count);
    }

    [Fact]
    public async Task The_stage_closes_when_the_host_ends_the_meeting()
    {
        var calendar = new CalendarViewModel(_world.NewSession());
        await calendar.LoadAsync();
        await calendar.JoinCommand.ExecuteAsync(calendar.Meetings[0]);

        _world.Api.Meetings.Clear();
        _world.Realtime.RaiseMeetingsChanged();

        Assert.False(calendar.IsInMeeting);
    }

    [Fact]
    public void Ended_meetings_are_not_joinable()
    {
        var active = _world.Api.Meetings[0];
        var ended = active with { IsActive = false, EndsAt = DateTimeOffset.Now.AddMinutes(-5) };
        var session = _world.NewSession();

        var view = new MeetingViewModel(ended, session.People);

        Assert.True(view.HasEnded);
        Assert.False(view.CanJoin);
        Assert.Contains("ended", view.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Leaving_returns_to_the_meeting_list()
    {
        var calendar = new CalendarViewModel(_world.NewSession());
        await calendar.LoadAsync();
        await calendar.JoinCommand.ExecuteAsync(calendar.Meetings[0]);

        await calendar.LeaveCommand.ExecuteAsync(null);

        Assert.False(calendar.IsInMeeting);
        Assert.DoesNotContain(calendar.Meetings.Single().Participants, p => p.IsMe);
    }
}

public class SignInFlowTests
{
    private readonly World _world = new();

    [Fact]
    public async Task Sign_in_needs_an_email_and_password()
    {
        var login = Assert.IsType<LoginViewModel>(_world.NewApp().Current);

        await login.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Enter your email and password.", login.ErrorMessage);
        Assert.False(_world.Auth.IsSignedIn);
    }

    [Fact]
    public async Task A_rejected_sign_in_shows_the_servers_reason_and_stays_on_the_form()
    {
        var app = _world.NewApp();
        var login = Assert.IsType<LoginViewModel>(app.Current);
        _world.Auth.Rejection = "The email or password is incorrect.";
        login.Email = "ana@example.com";
        login.Password = "wrong-password";

        await login.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("The email or password is incorrect.", login.ErrorMessage);
        Assert.Same(login, app.Current);
    }

    [Fact]
    public async Task A_saved_session_opens_the_shell_without_the_sign_in_form()
    {
        _world.Auth.HasSavedSession = true;
        var app = _world.NewApp();

        await app.ResumeSessionAsync();

        Assert.IsType<ShellViewModel>(app.Current);
    }

    [Fact]
    public async Task Without_a_saved_session_the_sign_in_form_stays()
    {
        var app = _world.NewApp();

        await app.ResumeSessionAsync();

        var login = Assert.IsType<LoginViewModel>(app.Current);
        Assert.False(login.IsBusy);
    }

    [Fact]
    public async Task Signing_in_opens_the_shell_on_chat()
    {
        var app = _world.NewApp();
        var login = Assert.IsType<LoginViewModel>(app.Current);
        login.Email = "ana@example.com";
        login.Password = "correct horse battery";

        await login.SubmitCommand.ExecuteAsync(null);

        var shell = Assert.IsType<ShellViewModel>(app.Current);
        Assert.Same(shell.Chat, shell.Current);
        Assert.Equal(2, shell.RailItems.Single(i => i.Label == "Chat").Badge);
        Assert.Equal(string.Empty, login.Password);
    }

    [Fact]
    public async Task Creating_an_account_registers_then_signs_in()
    {
        var app = _world.NewApp();
        var login = Assert.IsType<LoginViewModel>(app.Current);
        login.ToggleModeCommand.Execute(null);
        login.Email = " ana@example.com ";
        login.Password = "correct horse battery";
        login.DisplayName = "Ana Reyes";

        await login.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("ana@example.com", Assert.Single(_world.Api.Registered).Email);
        Assert.True(_world.Auth.IsSignedIn);
    }

    [Fact]
    public async Task Someone_with_no_organization_is_asked_to_create_or_join_one()
    {
        _world.Api.Organizations.Clear();
        var app = _world.NewApp();

        await app.EnterAsync();
        var setup = Assert.IsType<OrganizationSetupViewModel>(app.Current);
        setup.OrganizationName = "Northwind Studio";
        await setup.CreateCommand.ExecuteAsync(null);

        Assert.IsType<ShellViewModel>(app.Current);
    }

    [Fact]
    public async Task A_bad_invite_code_is_explained()
    {
        _world.Api.Organizations.Clear();
        var app = _world.NewApp();
        await app.EnterAsync();
        var setup = Assert.IsType<OrganizationSetupViewModel>(app.Current);

        setup.JoinCode = "NOPE";
        await setup.JoinCommand.ExecuteAsync(null);

        Assert.Equal("That invite code is not valid.", setup.ErrorMessage);
        Assert.Same(setup, app.Current);
    }

    [Fact]
    public async Task Signing_out_returns_to_sign_in_and_drops_the_connection()
    {
        var app = _world.NewApp();
        await app.EnterAsync();

        await app.SignOutAsync();

        Assert.IsType<LoginViewModel>(app.Current);
        Assert.False(_world.Realtime.IsConnected);
        Assert.False(_world.Auth.IsSignedIn);
    }
}

public class ShellViewModelTests
{
    private readonly World _world = new();

    private async Task<ShellViewModel> ShellAsync()
    {
        var app = _world.NewApp();
        await app.EnterAsync();
        return Assert.IsType<ShellViewModel>(app.Current);
    }

    [Fact]
    public async Task The_rail_follows_the_design_order()
    {
        var shell = await ShellAsync();

        Assert.Equal(
            ["Activity", "Chat", "Workspaces", "Calendar", "Calls", "Files", "Music"],
            shell.RailItems.Select(i => i.Label));
        Assert.Equal("Settings", shell.SettingsItem.Label);
    }

    [Fact]
    public async Task Navigating_selects_exactly_one_rail_item()
    {
        var shell = await ShellAsync();

        shell.NavigateCommand.Execute(shell.SettingsItem);

        Assert.Same(shell.Settings, shell.Current);
        Assert.True(shell.SettingsItem.IsSelected);
        Assert.DoesNotContain(shell.RailItems, i => i.IsSelected);
    }

    [Fact]
    public async Task Presence_changes_reach_everyone_showing_that_person()
    {
        var shell = await ShellAsync();
        var ben = shell.Chat.Conversations.Single(c => c.Id == _world.WithBen.Id).Other!;
        Assert.Equal(PresenceStatus.Available, ben.Status);

        _world.Realtime.RaisePresence(new PresenceDto(_world.Ben.Id, PresenceStatus.Offline, null));

        Assert.Equal(PresenceStatus.Offline, ben.Status);
    }

    [Fact]
    public async Task Reconnecting_catches_up_on_what_was_missed()
    {
        var shell = await ShellAsync();
        _world.Realtime.SetConnected(false);
        Assert.False(shell.IsConnected);

        // Arrives while offline, so no event is delivered for it.
        _world.Api.Conversations[2] = _world.WithPriya with { UnreadCount = 3, LastMessagePreview = "Call me when you can" };
        _world.Realtime.SetConnected(true);

        Assert.True(shell.IsConnected);
        Assert.Equal(3, shell.Chat.Conversations.Single(c => c.Id == _world.WithPriya.Id).UnreadCount);
    }

    [Fact]
    public async Task A_reminder_shows_while_in_a_meeting_and_looking_elsewhere()
    {
        var shell = await ShellAsync();
        shell.NavigateCommand.Execute(shell.RailItems.Single(i => i.Label == "Calendar"));
        await shell.Calendar.JoinCommand.ExecuteAsync(shell.Calendar.Meetings[0]);
        Assert.False(shell.ShowMeetingBanner);
        Assert.False(shell.ShowMeetingPip);

        shell.NavigateCommand.Execute(shell.RailItems.Single(i => i.Label == "Chat"));
        // Live media → Teams-style PiP replaces the text banner.
        Assert.True(shell.ShowMeetingPip);
        Assert.False(shell.ShowMeetingBanner);

        shell.ReturnToMeetingCommand.Execute(null);
        Assert.Same(shell.Calendar, shell.Current);
        Assert.False(shell.ShowMeetingPip);
    }
}

public class SettingsViewModelTests
{
    private readonly World _world = new();

    private SettingsViewModel Settings(Func<Task>? signOut = null, Func<Task>? onLeft = null) =>
        new(_world.NewSession(), _world.Api.Me, _world.Config,
            signOut ?? (() => Task.CompletedTask), onLeft ?? (() => Task.CompletedTask));

    [Fact]
    public async Task Saving_the_profile_updates_the_name_shown_everywhere()
    {
        var session = _world.NewSession();
        var settings = new SettingsViewModel(
            session, _world.Api.Me, _world.Config, () => Task.CompletedTask, () => Task.CompletedTask);

        settings.DisplayName = "Ana R.";
        await settings.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal("Ana R.", session.People.Me.DisplayName);
        Assert.Equal("Saved", settings.ProfileStatus);
    }

    [Fact]
    public void Choosing_an_availability_tells_the_server()
    {
        var settings = Settings();

        settings.Availability = settings.AvailabilityOptions.Single(o => o.Status == PresenceStatus.Busy);

        Assert.Equal(PresenceStatus.Busy, _world.Realtime.LastAvailability);
    }

    [Fact]
    public async Task The_invite_code_can_be_copied()
    {
        var settings = Settings();

        await settings.CopyJoinCodeCommand.ExecuteAsync(null);

        Assert.Equal("K7MPQ-2XHWD", _world.Platform.Clipboard);
    }

    [Fact]
    public async Task Leaving_asks_for_confirmation_then_calls_the_api()
    {
        var left = false;
        var settings = Settings(onLeft: () => { left = true; return Task.CompletedTask; });

        await settings.LeaveOrganizationCommand.ExecuteAsync(null);

        Assert.Single(_world.Platform.Confirms);
        Assert.Equal("Leave organization?", _world.Platform.Confirms[0].Title);
        Assert.Empty(_world.Api.Organizations);
        Assert.True(left);
        Assert.Null(settings.LeaveStatus);
    }

    [Fact]
    public async Task Cancelling_the_leave_confirmation_does_nothing()
    {
        _world.Platform.NextConfirmResult = false;
        var left = false;
        var settings = Settings(onLeft: () => { left = true; return Task.CompletedTask; });

        await settings.LeaveOrganizationCommand.ExecuteAsync(null);

        Assert.Single(_world.Platform.Confirms);
        Assert.Contains(_world.Api.Organizations, o => o.Id == _world.Organization.Id);
        Assert.False(left);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData("Ana Reyes", "AR")]
    [InlineData("ana", "A")]
    [InlineData("  Mary  Jane   Watson ", "MW")]
    [InlineData("Test Account Three (you)", "TT")]
    [InlineData("Test Account Three (You)", "TT")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials_come_from_the_first_and_last_names(string? name, string expected) =>
        Assert.Equal(expected, Avatar.InitialsOf(name));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(184_320, "180 KB")]
    [InlineData(2_411_724, "2.3 MB")]
    public void File_sizes_are_readable(long bytes, string expected) =>
        Assert.Equal(expected, Describe.FileSize(bytes));

    [Theory]
    [InlineData("image/png", "x.bin", true)]
    [InlineData("application/octet-stream", "photo.JPG", true)]
    [InlineData("application/octet-stream", "notes.txt", false)]
    [InlineData("image/svg+xml", "icon.svg", false)]
    public void Image_attachments_are_detected_from_type_or_extension(
        string contentType, string fileName, bool expected) =>
        Assert.Equal(expected, FileTypes.IsImage(contentType, fileName));

    [Fact]
    public void Png_magic_bytes_are_sniffed_when_the_name_has_no_extension()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        using var stream = new MemoryStream(png);
        Assert.Equal("image/png", FileTypes.ResolveContentType("01d7f8f8-fcba-47f1-be27", stream));
    }

    [Fact]
    public void Times_are_relative_to_today()
    {
        var now = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 23, 15, 0, 0)));

        Assert.Equal("Yesterday", Describe.Time(now.AddDays(-1), now));
        Assert.Equal("Nov 2", Describe.Time(now.AddDays(-21), now));
        Assert.Equal("Dec 1, 2025", Describe.Time(new DateTimeOffset(2025, 12, 1, 12, 0, 0, now.Offset), now));
    }

    [Fact]
    public void Server_errors_are_shown_as_written_and_network_errors_are_explained()
    {
        Assert.Equal("Nope.", Describe.Error(new ApiException("Nope.", 400)));
        Assert.StartsWith("Can't reach the MeetJoe server", Describe.Error(new HttpRequestException()));
    }
}

public class MeetingReminderTests
{
    private readonly World _world = new();

    [Fact]
    public void Reminder_fires_inside_the_ten_minute_window_for_an_invitee()
    {
        var now = DateTimeOffset.Parse("2026-11-27T08:00:00Z");
        var meeting = FakeApi.FakeMeeting(
            Guid.NewGuid(), "EOG Demo", _world.Ben, now.AddDays(-1), now.AddMinutes(10), true, [],
            invites: [new MeetingInviteDto(_world.Ana.Id, "Ana", InviteStatus.Accepted)]);

        Assert.True(MeetingReminderRules.ShouldNotify(meeting, _world.Ana.Id, now, new HashSet<Guid>()));
        Assert.False(MeetingReminderRules.ShouldNotify(
            meeting, _world.Ana.Id, now.AddMinutes(-1), new HashSet<Guid>())); // 11 min before
        Assert.False(MeetingReminderRules.ShouldNotify(
            meeting, _world.Ana.Id, now.AddMinutes(10), new HashSet<Guid>())); // at start
    }

    [Fact]
    public void Declined_invitees_are_not_reminded()
    {
        var now = DateTimeOffset.UtcNow;
        var meeting = FakeApi.FakeMeeting(
            Guid.NewGuid(), "Skip me", _world.Ben, now.AddDays(-1), now.AddMinutes(5), true, [],
            invites: [new MeetingInviteDto(_world.Ana.Id, "Ana", InviteStatus.Declined)]);

        Assert.False(MeetingReminderRules.ShouldNotify(meeting, _world.Ana.Id, now, new HashSet<Guid>()));
    }

    [Fact]
    public async Task Watcher_posts_a_native_os_notification_once()
    {
        var now = DateTimeOffset.UtcNow;
        var meeting = FakeApi.FakeMeeting(
            Guid.NewGuid(), "EOG Demo", _world.Ana, now.AddDays(-1), now.AddMinutes(8), true, [],
            invites: [new MeetingInviteDto(_world.Ben.Id, "Ben", InviteStatus.Pending)]);
        _world.Api.Meetings.Add(meeting);

        var notifications = new RecordingOsNotificationService();
        using var watcher = new MeetingReminderWatcher(
            _world.Api,
            notifications,
            () => _world.Organization.Id,
            () => _world.Ana.Id);

        await watcher.TickAsync();
        await watcher.TickAsync();

        var alert = Assert.Single(notifications.Items);
        Assert.Equal("MeetJoe", alert.Title);
        Assert.Contains("EOG Demo", alert.Body);
        Assert.Contains("minutes", alert.Body);
    }
}
