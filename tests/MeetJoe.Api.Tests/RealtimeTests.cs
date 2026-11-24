using MeetJoe.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace MeetJoe.Api.Tests;

[Collection(ApiCollection.Name)]
public class RealtimeTests(ApiFactory api)
{
    private static Inbox<T> Listen<T>(HubConnection hub, string eventName)
    {
        var inbox = new Inbox<T>();
        hub.On<T>(eventName, inbox.Add);
        return inbox;
    }

    private static Inbox<string> ListenFor(HubConnection hub, string eventName)
    {
        var inbox = new Inbox<string>();
        hub.On(eventName, () => inbox.Add(eventName));
        return inbox;
    }

    [Theory]
    [InlineData(HubPaths.Chat)]
    [InlineData(HubPaths.Presence)]
    [InlineData(HubPaths.Notifications)]
    public async Task Hubs_refuse_connections_without_a_token(string path)
    {
        await using var hub = api.NewHub(path, accessToken: null);

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => hub.StartAsync());

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task A_message_sent_over_the_hub_reaches_the_other_person_and_the_senders_other_devices()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var (ana, ben, chloe) = (people[0], people[1], people[2]);
        var conversation = await ana.PostAsync<ConversationDto>(
            $"/api/orgs/{org.Id}/conversations", new CreateConversationRequest([ben.Id], null));

        await using var anaLaptop = await ana.ConnectAsync(HubPaths.Chat);
        await using var anaPhone = await ana.ConnectAsync(HubPaths.Chat);
        await using var benHub = await ben.ConnectAsync(HubPaths.Chat);
        await using var chloeHub = await chloe.ConnectAsync(HubPaths.Chat);
        var onPhone = Listen<MessageDto>(anaPhone, ChatHubEvents.MessageReceived);
        var forBen = Listen<MessageDto>(benHub, ChatHubEvents.MessageReceived);
        var forChloe = Listen<MessageDto>(chloeHub, ChatHubEvents.MessageReceived);

        var sent = await anaLaptop.InvokeAsync<MessageDto>(
            ChatHubMethods.SendMessage, new SendMessageRequest(conversation.Id, null, "Lunch?", null));

        Assert.Equal("Lunch?", sent.Body);
        Assert.Equal(sent.Id, (await forBen.NextAsync()).Id);
        Assert.Equal(sent.Id, (await onPhone.NextAsync()).Id);
        Assert.True(await forChloe.StaysEmptyAsync());

        // Persisted, not just relayed.
        Assert.Equal(sent.Id, Assert.Single(await ben.GetAsync<List<MessageDto>>($"/api/conversations/{conversation.Id}/messages")).Id);
    }

    [Fact]
    public async Task A_message_posted_over_rest_is_pushed_too()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));
        await ana.PostAsync($"/api/workspaces/{workspace.Id}/members", new AddWorkspaceMemberRequest(ben.Id));
        await using var benHub = await ben.ConnectAsync(HubPaths.Chat);
        var forBen = Listen<MessageDto>(benHub, ChatHubEvents.MessageReceived);

        var posted = await ana.SendAsync(null, workspace.Channels[0].Id, "Release notes are up");

        var received = await forBen.NextAsync();
        Assert.Equal((posted.Id, workspace.Channels[0].Id), (received.Id, received.ChannelId!.Value));
    }

    [Fact]
    public async Task Hub_callers_are_told_why_a_message_was_refused_but_not_shown_internals()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var conversation = await people[0].PostAsync<ConversationDto>(
            $"/api/orgs/{org.Id}/conversations", new CreateConversationRequest([people[1].Id], null));
        await using var anaHub = await people[0].ConnectAsync(HubPaths.Chat);
        await using var chloeHub = await people[2].ConnectAsync(HubPaths.Chat);

        var empty = await Assert.ThrowsAsync<HubException>(() => anaHub.InvokeAsync<MessageDto>(
            ChatHubMethods.SendMessage, new SendMessageRequest(conversation.Id, null, "  ", null)));
        var notMember = await Assert.ThrowsAsync<HubException>(() => chloeHub.InvokeAsync<MessageDto>(
            ChatHubMethods.SendMessage, new SendMessageRequest(conversation.Id, null, "Hi", null)));

        Assert.Contains("A message needs text or at least one attachment.", empty.Message);
        Assert.Contains("Conversation was not found.", notMember.Message);
        Assert.DoesNotContain("   at ", empty.Message); // No stack trace.
    }

    [Fact]
    public async Task Colleagues_see_someone_come_online_change_status_and_go_offline()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var stranger = await api.NewUserAsync("Mallory");
        await using var anaHub = await ana.ConnectAsync(HubPaths.Presence);
        await using var strangerHub = await stranger.ConnectAsync(HubPaths.Presence);
        var forAna = Listen<PresenceDto>(anaHub, PresenceHubEvents.PresenceChanged);
        var forStranger = Listen<PresenceDto>(strangerHub, PresenceHubEvents.PresenceChanged);

        Assert.Equal(PresenceStatus.Offline, await StatusOfAsync(ana, org, ben));

        var benHub = await ben.ConnectAsync(HubPaths.Presence);
        Assert.Equal(PresenceStatus.Available, (await forAna.NextAsync(p => p.UserId == ben.Id)).Status);
        Assert.Equal(PresenceStatus.Available, await StatusOfAsync(ana, org, ben));

        var busy = await benHub.InvokeAsync<PresenceDto>(PresenceHubMethods.SetAvailability, PresenceStatus.Busy);
        Assert.Equal(PresenceStatus.Busy, busy.Status);
        Assert.Equal(PresenceStatus.Busy, (await forAna.NextAsync(p => p.UserId == ben.Id)).Status);

        await benHub.DisposeAsync();
        Assert.Equal(PresenceStatus.Offline, (await forAna.NextAsync(p => p.UserId == ben.Id)).Status);
        Assert.Equal(PresenceStatus.Offline, await StatusOfAsync(ana, org, ben));

        // Someone in no shared organization hears about themselves only.
        await Task.Delay(300);
        Assert.All(forStranger.Everything, presence => Assert.Equal(stranger.Id, presence.UserId));
    }

    [Fact]
    public async Task Someone_with_two_devices_stays_online_until_the_last_one_disconnects()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var laptop = await ben.ConnectAsync(HubPaths.Presence);
        var phone = await ben.ConnectAsync(HubPaths.Presence);

        await laptop.DisposeAsync();
        Assert.Equal(PresenceStatus.Available, await StatusOfAsync(ana, org, ben));

        await phone.DisposeAsync();
        await WaitForAsync(async () => await StatusOfAsync(ana, org, ben) == PresenceStatus.Offline);
    }

    [Fact]
    public async Task Offline_is_not_something_a_person_can_choose()
    {
        var ana = await api.NewUserAsync("Ana");
        await using var hub = await ana.ConnectAsync(HubPaths.Presence);

        var error = await Assert.ThrowsAsync<HubException>(
            () => hub.InvokeAsync<PresenceDto>(PresenceHubMethods.SetAvailability, PresenceStatus.Offline));
        Assert.Contains("Choose Available, Away, Busy or Do not disturb.", error.Message);

        await ResponseAssert.ProblemAsync(
            ana.PutAsync("/api/me/presence", new SetPresenceRequest(PresenceStatus.Offline)), 400, "validation");
    }

    [Fact]
    public async Task People_are_told_when_their_lists_change()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        await using var benHub = await ben.ConnectAsync(HubPaths.Notifications);
        var conversations = ListenFor(benHub, NotificationHubEvents.ConversationsChanged);
        var workspaces = ListenFor(benHub, NotificationHubEvents.WorkspacesChanged);
        var meetings = ListenFor(benHub, NotificationHubEvents.MeetingsChanged);
        var members = ListenFor(benHub, NotificationHubEvents.MembersChanged);

        // A new conversation that includes Ben
        await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", new CreateConversationRequest([ben.Id], null));
        await conversations.NextAsync();

        // A workspace Ben is not in says nothing; being added to it does.
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));
        Assert.True(await workspaces.StaysEmptyAsync());
        await ana.PostAsync($"/api/workspaces/{workspace.Id}/members", new AddWorkspaceMemberRequest(ben.Id));
        await workspaces.NextAsync();
        await ana.PostAsync<ChannelDto>($"/api/workspaces/{workspace.Id}/channels", new CreateChannelRequest("Roadmap", null));
        await workspaces.NextAsync();

        // Meetings starting, filling and ending
        var meeting = await ana.PostAsync<MeetingDto>($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("Standup"));
        await meetings.NextAsync();
        await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        await meetings.NextAsync();
        await ana.PostAsync($"/api/meetings/{meeting.Id}/end");
        await meetings.NextAsync();

        // Someone new joining the organization
        var chloe = await api.NewUserAsync("Chloe");
        await chloe.PostAsync<OrganizationDto>("/api/orgs/join", new JoinOrganizationRequest(org.JoinCode!));
        await members.NextAsync();
    }

    private static async Task<PresenceStatus> StatusOfAsync(TestUser viewer, OrganizationDto org, TestUser subject) =>
        (await viewer.GetAsync<List<OrgMemberDto>>($"/api/orgs/{org.Id}/members")).Single(m => m.UserId == subject.Id).Status;

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail("The expected state was not reached in time.");
    }
}
