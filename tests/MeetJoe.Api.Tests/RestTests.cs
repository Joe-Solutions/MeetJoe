using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MeetJoe.Application.Calls;
using MeetJoe.Contracts;
using MeetJoe.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeetJoe.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory api)
{
    [Fact]
    public async Task Health_needs_no_sign_in()
    {
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public async Task The_api_refuses_requests_without_a_valid_token(string? token)
    {
        using var client = api.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/orgs")).StatusCode);
    }

    [Fact]
    public async Task A_new_account_can_sign_in_and_read_its_profile()
    {
        var ana = await api.NewUserAsync("Ana Reyes");

        var me = await ana.GetAsync<MeDto>("/api/me");

        Assert.Equal(ana.Id, me.Id);
        Assert.Equal(ana.Email, me.Email);
        Assert.Equal("Ana Reyes", me.DisplayName);
        Assert.Equal(PresenceStatus.Available, me.Availability);
    }

    [Fact]
    public async Task Short_passwords_are_refused_with_the_reason()
    {
        using var client = api.CreateClient();

        var detail = await ResponseAssert.ProblemAsync(
            client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"x-{Guid.NewGuid():N}@example.com", "short", "X")),
            400, "validation");

        Assert.Contains("at least 10 characters", detail);
    }

    [Fact]
    public async Task An_email_can_only_be_registered_once_and_says_so_once()
    {
        var ana = await api.NewUserAsync("Ana");
        using var client = api.CreateClient();

        var detail = await ResponseAssert.ProblemAsync(
            client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(ana.Email, ApiFactory.Password, "Imposter")),
            400, "validation");

        Assert.Equal($"Email '{ana.Email}' is already taken.", detail);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_email_get_the_same_answer()
    {
        var ana = await api.NewUserAsync("Ana");
        using var client = api.CreateClient();

        var wrongPassword = await TokenErrorAsync(client, ana.Email, "not the password");
        var unknownEmail = await TokenErrorAsync(client, $"nobody-{Guid.NewGuid():N}@example.com", ApiFactory.Password);

        Assert.Equal("The email or password is incorrect.", wrongPassword);
        Assert.Equal(wrongPassword, unknownEmail);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_one()
    {
        var ana = await api.NewUserAsync("Ana");
        using var client = api.CreateClient();

        for (var i = 0; i < 5; i++) await TokenErrorAsync(client, ana.Email, "not the password");
        var afterLockout = await TokenErrorAsync(client, ana.Email, ApiFactory.Password);

        Assert.StartsWith("Too many failed sign-in attempts", afterLockout);
    }

    [Fact]
    public async Task A_refresh_token_buys_a_new_access_token()
    {
        var ana = await api.NewUserAsync("Ana");
        using var client = api.CreateClient();

        var refreshed = await ApiFactory.RequestTokenAsync(client, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = AuthConstants.DesktopClientId,
            ["refresh_token"] = ana.RefreshToken,
        });

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", refreshed.GetProperty("access_token").GetString());
        Assert.Equal(ana.Id, (await client.GetFromJsonAsync<MeDto>("/api/me", ApiFactory.Json))!.Id);
    }

    [Fact]
    public async Task Unknown_clients_cannot_request_tokens()
    {
        var ana = await api.NewUserAsync("Ana");
        using var client = api.CreateClient();

        var response = await client.PostAsync(AuthConstants.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "someone-else",
            ["username"] = ana.Email,
            ["password"] = ApiFactory.Password,
        }));

        Assert.False(response.IsSuccessStatusCode);
    }

    private static async Task<string> TokenErrorAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsync(AuthConstants.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = AuthConstants.DesktopClientId,
            ["username"] = email,
            ["password"] = password,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
        return body.GetProperty("error_description").GetString()!;
    }
}

[Collection(ApiCollection.Name)]
public class OrganizationTests(ApiFactory api)
{
    [Fact]
    public async Task The_creator_owns_the_organization_and_can_see_its_invite_code()
    {
        var ana = await api.NewUserAsync("Ana");

        var created = await ana.PostAsync<OrganizationDto>("/api/orgs", new CreateOrganizationRequest("  Northwind  "));
        var mine = await ana.GetAsync<List<OrganizationDto>>("/api/orgs");

        Assert.Equal("Northwind", created.Name);
        Assert.Equal(OrgRole.Owner, created.Role);
        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", created.JoinCode);
        Assert.Equal(created.Id, Assert.Single(mine).Id);
    }

    [Fact]
    public async Task People_join_with_the_code_however_they_type_it_and_do_not_see_it_afterwards()
    {
        var ana = await api.NewUserAsync("Ana");
        var ben = await api.NewUserAsync("Ben");
        var organization = await ana.PostAsync<OrganizationDto>("/api/orgs", new CreateOrganizationRequest("Northwind"));
        var typed = organization.JoinCode!.Replace("-", " ").ToLowerInvariant();

        var joined = await ben.PostAsync<OrganizationDto>("/api/orgs/join", new JoinOrganizationRequest(typed));
        var joinedAgain = await ben.PostAsync<OrganizationDto>("/api/orgs/join", new JoinOrganizationRequest(typed));

        Assert.Equal(organization.Id, joined.Id);
        Assert.Equal(OrgRole.Member, joined.Role);
        Assert.Null(joined.JoinCode);
        Assert.Equal(organization.Id, joinedAgain.Id);

        var members = await ben.GetAsync<List<OrgMemberDto>>($"/api/orgs/{organization.Id}/members");
        Assert.Equal(["Ana", "Ben"], members.Select(m => m.DisplayName).Order());
    }

    [Fact]
    public async Task A_member_can_leave_but_the_sole_owner_cannot()
    {
        var ana = await api.NewUserAsync("Ana");
        var ben = await api.NewUserAsync("Ben");
        var organization = await ana.PostAsync<OrganizationDto>("/api/orgs", new CreateOrganizationRequest("Northwind"));
        await ben.PostAsync<OrganizationDto>("/api/orgs/join", new JoinOrganizationRequest(organization.JoinCode!));

        Assert.Equal(HttpStatusCode.NoContent, (await ben.PostAsync($"/api/orgs/{organization.Id}/leave")).StatusCode);
        Assert.Empty(await ben.GetAsync<List<OrganizationDto>>("/api/orgs"));

        var members = await ana.GetAsync<List<OrgMemberDto>>($"/api/orgs/{organization.Id}/members");
        Assert.Equal(["Ana"], members.Select(m => m.DisplayName));

        var detail = await ResponseAssert.ProblemAsync(
            ana.PostAsync($"/api/orgs/{organization.Id}/leave"), 400, "validation");
        Assert.Equal("Add another owner before leaving this organization.", detail);
    }

    [Theory]
    [InlineData("AAAAA-AAAAA")]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task A_wrong_invite_code_is_refused(string code)
    {
        var ben = await api.NewUserAsync("Ben");

        var detail = await ResponseAssert.ProblemAsync(
            ben.PostAsync("/api/orgs/join", new JoinOrganizationRequest(code)), 400, "validation");

        Assert.Equal("That invite code is not valid.", detail);
    }

    [Fact]
    public async Task Outsiders_cannot_tell_whether_an_organization_exists()
    {
        var (organization, _) = await api.NewOrganizationAsync("Ana");
        var outsider = await api.NewUserAsync("Mallory");

        var real = await ResponseAssert.ProblemAsync(outsider.Client.GetAsync($"/api/orgs/{organization.Id}/members"), 404, "not_found");
        var made_up = await ResponseAssert.ProblemAsync(outsider.Client.GetAsync($"/api/orgs/{Guid.NewGuid()}/members"), 404, "not_found");

        Assert.Equal(real, made_up);
        foreach (var list in new[] { "conversations", "workspaces", "files", "meetings" })
            await ResponseAssert.ProblemAsync(outsider.Client.GetAsync($"/api/orgs/{organization.Id}/{list}"), 404, "not_found");
    }

    [Fact]
    public async Task Profile_changes_are_validated_and_saved()
    {
        var ana = await api.NewUserAsync("Ana");

        await ResponseAssert.ProblemAsync(ana.PutAsync("/api/me", new UpdateProfileRequest("  ", null)), 400, "validation");
        var saved = await ana.PutAsync("/api/me", new UpdateProfileRequest(" Ana Reyes ", " In a workshop "));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var me = await ana.GetAsync<MeDto>("/api/me");
        Assert.Equal(("Ana Reyes", "In a workshop"), (me.DisplayName, me.StatusMessage));
    }
}

[Collection(ApiCollection.Name)]
public class ChatTests(ApiFactory api)
{
    private static CreateConversationRequest With(params TestUser[] people) => new(people.Select(p => p.Id).ToList(), null);

    [Fact]
    public async Task Two_people_share_one_direct_conversation_whoever_opens_it()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);

        var fromAna = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));
        var fromBen = await ben.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ana));

        Assert.Equal(fromAna.Id, fromBen.Id);
        Assert.Equal(ConversationKind.Direct, fromAna.Kind);
        Assert.Equal(2, fromAna.Members.Count);
    }

    [Fact]
    public async Task Two_people_opening_the_same_chat_at_once_still_get_one_conversation()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);

        var results = await Task.WhenAll(
            ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben)),
            ben.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ana)),
            ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben)));

        Assert.Single(results.Select(c => c.Id).Distinct());
        Assert.Single(await ana.GetAsync<List<ConversationDto>>($"/api/orgs/{org.Id}/conversations"));
    }

    [Fact]
    public async Task Groups_can_be_named_and_are_not_merged()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var request = new CreateConversationRequest([people[1].Id, people[2].Id], "Launch planning");

        var first = await people[0].PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", request);
        var second = await people[0].PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", request);

        Assert.Equal(ConversationKind.Group, first.Kind);
        Assert.Equal("Launch planning", first.Title);
        Assert.Equal(3, first.Members.Count);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Conversations_only_include_people_from_the_organization()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana");
        var outsider = await api.NewUserAsync("Mallory");

        await ResponseAssert.ProblemAsync(
            people[0].PostAsync($"/api/orgs/{org.Id}/conversations", With(outsider)), 400, "validation");
        await ResponseAssert.ProblemAsync(
            people[0].PostAsync($"/api/orgs/{org.Id}/conversations", With(people[0])), 400, "validation");
    }

    [Fact]
    public async Task Messages_arrive_in_order_raise_unread_counts_and_reading_clears_them()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var conversation = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));

        var first = await ana.SendAsync(conversation.Id, null, "  Hello Ben  ");
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        await ana.SendAsync(conversation.Id, null, "Are you free at 3?");

        Assert.Equal("Hello Ben", first.Body);
        Assert.Equal(ana.Id, first.Sender.Id);

        var forBen = Assert.Single(await ben.GetAsync<List<ConversationDto>>($"/api/orgs/{org.Id}/conversations"));
        Assert.Equal(2, forBen.UnreadCount);
        Assert.Equal("Are you free at 3?", forBen.LastMessagePreview);
        var forAna = Assert.Single(await ana.GetAsync<List<ConversationDto>>($"/api/orgs/{org.Id}/conversations"));
        Assert.Equal(0, forAna.UnreadCount);

        var history = await ben.GetAsync<List<MessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.Equal(["Hello Ben", "Are you free at 3?"], history.Select(m => m.Body));

        Assert.Equal(HttpStatusCode.NoContent, (await ben.PostAsync($"/api/conversations/{conversation.Id}/read")).StatusCode);
        Assert.Equal(0, Assert.Single(await ben.GetAsync<List<ConversationDto>>($"/api/orgs/{org.Id}/conversations")).UnreadCount);
    }

    [Fact]
    public async Task History_pages_backwards_from_a_cursor()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var conversation = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));
        // Distinct CreatedAt values — paging cursors are timestamps (see ChatService.GetMessagesAsync).
        for (var i = 1; i <= 5; i++)
        {
            await ana.SendAsync(conversation.Id, null, $"message {i}");
            api.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var latest = await ben.GetAsync<List<MessageDto>>($"/api/conversations/{conversation.Id}/messages?limit=2");
        var before = Uri.EscapeDataString(latest[0].CreatedAt.ToString("O"));
        var older = await ben.GetAsync<List<MessageDto>>($"/api/conversations/{conversation.Id}/messages?limit=2&before={before}");

        Assert.Equal(["message 4", "message 5"], latest.Select(m => m.Body));
        Assert.Equal(["message 2", "message 3"], older.Select(m => m.Body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_message_is_refused(string body)
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var conversation = await people[0].PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(people[1]));

        await ResponseAssert.ProblemAsync(
            people[0].PostAsync("/api/messages", new SendMessageRequest(conversation.Id, null, body, null)), 400, "validation");
    }

    [Fact]
    public async Task People_outside_a_conversation_cannot_read_post_or_mark_it()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var (ana, ben, chloe) = (people[0], people[1], people[2]);
        var conversation = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));
        await ana.SendAsync(conversation.Id, null, "Between us");

        await ResponseAssert.ProblemAsync(chloe.Client.GetAsync($"/api/conversations/{conversation.Id}/messages"), 404, "not_found");
        await ResponseAssert.ProblemAsync(
            chloe.PostAsync("/api/messages", new SendMessageRequest(conversation.Id, null, "Hi!", null)), 404, "not_found");
        await ResponseAssert.ProblemAsync(chloe.PostAsync($"/api/conversations/{conversation.Id}/read"), 404, "not_found");
        Assert.Empty(await chloe.GetAsync<List<ConversationDto>>($"/api/orgs/{org.Id}/conversations"));
    }

    [Fact]
    public async Task Messages_can_be_edited_deleted_replied_to_and_reacted()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var conversation = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));
        var root = await ana.SendAsync(conversation.Id, null, "Draft");

        var edited = await ana.PutAsync<MessageDto>($"/api/messages/{root.Id}", new EditMessageRequest("Ship it"));
        Assert.Equal("Ship it", edited.Body);
        Assert.NotNull(edited.EditedAt);

        var reply = await ben.PostAsync<MessageDto>(
            "/api/messages", new SendMessageRequest(conversation.Id, null, "Looks good", null, root.Id));
        Assert.Equal(root.Id, reply.ParentMessageId);
        Assert.Equal("Ana", reply.ParentSender?.DisplayName);
        Assert.Equal("Ship it", reply.ParentBody);

        var history = await ana.GetAsync<List<MessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.Equal(2, history.Count);
        Assert.Equal(1, history.Single(m => m.Id == root.Id).ReplyCount);
        Assert.Contains(history, m => m.Id == reply.Id && m.ParentMessageId == root.Id);
        Assert.Equal("Looks good", Assert.Single(await ana.GetAsync<List<MessageDto>>($"/api/messages/{root.Id}/replies")).Body);

        var reacted = await ben.PostAsync<MessageDto>($"/api/messages/{root.Id}/reactions", new ToggleReactionRequest("👍"));
        var thumb = Assert.Single(reacted.Reactions!);
        Assert.Equal("👍", thumb.Emoji);
        Assert.Equal(1, thumb.Count);

        var deleted = await ana.DeleteAsync<MessageDto>($"/api/messages/{root.Id}");
        Assert.True(deleted.IsDeleted);
        Assert.Equal(string.Empty, deleted.Body);

        var activity = await ana.GetAsync<List<ActivityDto>>($"/api/orgs/{org.Id}/activity");
        Assert.Contains(activity, a => a.Kind == ActivityKind.Reply);
        Assert.Contains(activity, a => a.Kind == ActivityKind.Reaction);
    }
}

[Collection(ApiCollection.Name)]
public class CallTests(ApiFactory api)
{
    private static CreateConversationRequest With(TestUser other) => new([other.Id], null);

    [Fact]
    public async Task A_direct_call_can_be_answered_and_shows_in_history()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var conversation = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));

        var join = await ana.PostAsync<CallJoinDto>(
            $"/api/orgs/{org.Id}/calls", new StartCallRequest(conversation.Id));
        Assert.False(string.IsNullOrWhiteSpace(join.Token));
        Assert.StartsWith("call-", join.RoomName);

        var answered = await ben.PostAsync<CallJoinDto>($"/api/calls/{join.CallId}/answer");
        Assert.Equal(join.CallId, answered.CallId);

        var history = await ana.GetAsync<List<CallDto>>($"/api/orgs/{org.Id}/calls");
        var call = Assert.Single(history, c => c.Id == join.CallId);
        Assert.True(call.IsActive);
        Assert.Contains(call.Participants, p => p.UserId == ben.Id && p.Status == CallParticipantStatus.Joined);

        Assert.Equal(HttpStatusCode.NoContent, (await ana.PostAsync($"/api/calls/{join.CallId}/hangup")).StatusCode);
        var ended = Assert.Single(await ana.GetAsync<List<CallDto>>($"/api/orgs/{org.Id}/calls"), c => c.Id == join.CallId);
        Assert.False(ended.IsActive);
    }

    [Fact]
    public async Task Hanging_up_before_answer_creates_a_missed_call_activity()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var conversation = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));

        var join = await ana.PostAsync<CallJoinDto>(
            $"/api/orgs/{org.Id}/calls", new StartCallRequest(conversation.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await ana.PostAsync($"/api/calls/{join.CallId}/end")).StatusCode);

        var activity = await ben.GetAsync<List<ActivityDto>>($"/api/orgs/{org.Id}/activity");
        Assert.Contains(activity, a => a.Kind == ActivityKind.MissedCall && a.CallId == join.CallId);
    }

    [Fact]
    public async Task An_unanswered_call_expires_after_thirty_seconds()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var conversation = await ana.PostAsync<ConversationDto>($"/api/orgs/{org.Id}/conversations", With(ben));

        var join = await ana.PostAsync<CallJoinDto>(
            $"/api/orgs/{org.Id}/calls", new StartCallRequest(conversation.Id));

        api.Clock.Advance(TimeSpan.FromSeconds(31));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var calls = scope.ServiceProvider.GetRequiredService<CallService>();
            Assert.Equal(1, await calls.ExpireUnansweredRingsAsync());
        }

        var ended = Assert.Single(await ana.GetAsync<List<CallDto>>($"/api/orgs/{org.Id}/calls"), c => c.Id == join.CallId);
        Assert.False(ended.IsActive);
        Assert.Contains(ended.Participants, p => p.UserId == ben.Id && p.Status == CallParticipantStatus.Missed);

        var activity = await ben.GetAsync<List<ActivityDto>>($"/api/orgs/{org.Id}/activity");
        Assert.Contains(activity, a => a.Kind == ActivityKind.MissedCall && a.CallId == join.CallId);

        // A new call can start once the timed-out ring is cleared.
        var next = await ana.PostAsync<CallJoinDto>(
            $"/api/orgs/{org.Id}/calls", new StartCallRequest(conversation.Id));
        Assert.NotEqual(join.CallId, next.CallId);
    }
}

[Collection(ApiCollection.Name)]
public class FileTests(ApiFactory api)
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("<script>alert('hi')</script>");

    private async Task<(TestUser Ana, TestUser Ben, TestUser Chloe, OrganizationDto Org, ConversationDto Conversation)> SetupAsync()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var conversation = await people[0].PostAsync<ConversationDto>(
            $"/api/orgs/{org.Id}/conversations", new CreateConversationRequest([people[1].Id], null));
        return (people[0], people[1], people[2], org, conversation);
    }

    [Fact]
    public async Task An_uploaded_file_can_be_attached_and_downloaded_by_the_other_person()
    {
        var (ana, ben, _, org, conversation) = await SetupAsync();

        var file = await ana.UploadAsync($"/api/conversations/{conversation.Id}/files", "notes.html", Content);
        var message = await ana.SendAsync(conversation.Id, null, null, file.Id);

        var attachment = Assert.Single(message.Attachments);
        Assert.Equal(("notes.html", (long)Content.Length), (attachment.FileName, attachment.Size));

        var download = await ben.Client.GetAsync($"/api/files/{file.Id}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Content, await download.Content.ReadAsByteArrayAsync());

        var listed = Assert.Single(await ben.GetAsync<List<FileDto>>($"/api/orgs/{org.Id}/files"));
        Assert.Equal(file.Id, listed.Id);
        Assert.Equal("Sent an attachment",
            Assert.Single(await ben.GetAsync<List<ConversationDto>>($"/api/orgs/{org.Id}/conversations")).LastMessagePreview);
    }

    [Fact]
    public async Task Downloads_are_never_rendered_by_a_browser_whatever_the_uploader_claimed()
    {
        var (ana, ben, _, _, conversation) = await SetupAsync();
        var file = await ana.UploadAsync($"/api/conversations/{conversation.Id}/files", "page.html", Content);
        await ana.SendAsync(conversation.Id, null, "see attached", file.Id);

        var download = await ben.Client.GetAsync($"/api/files/{file.Id}/content");

        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(Content.Length, download.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData(@"..\..\windows\system.ini", "system.ini")]
    public async Task Directory_tricks_in_a_file_name_do_not_reach_storage(string supplied, string expected)
    {
        var (ana, _, _, org, conversation) = await SetupAsync();

        var file = await ana.UploadAsync($"/api/conversations/{conversation.Id}/files", supplied, Content);

        Assert.Equal(expected, file.FileName);
        Assert.Contains($"{org.Id:N}/{file.Id:N}", api.Storage.Keys);
        Assert.DoesNotContain(api.Storage.Keys, key => key.Contains("..") || key.Contains("passwd") || key.Contains("system"));
    }

    [Fact]
    public async Task Only_the_uploader_can_attach_a_file_and_only_once_and_only_where_it_was_uploaded()
    {
        var (ana, ben, _, org, conversation) = await SetupAsync();
        var file = await ana.UploadAsync($"/api/conversations/{conversation.Id}/files", "notes.txt", Content);

        // Someone else in the conversation
        await ResponseAssert.ProblemAsync(
            ben.PostAsync("/api/messages", new SendMessageRequest(conversation.Id, null, "mine now", [file.Id])), 400, "validation");

        // A different place
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));
        await ResponseAssert.ProblemAsync(
            ana.PostAsync("/api/messages", new SendMessageRequest(null, workspace.Channels[0].Id, "cross-post", [file.Id])), 400, "validation");

        // Twice
        await ana.SendAsync(conversation.Id, null, "first", file.Id);
        await ResponseAssert.ProblemAsync(
            ana.PostAsync("/api/messages", new SendMessageRequest(conversation.Id, null, "again", [file.Id])), 400, "validation");
    }

    [Fact]
    public async Task People_outside_the_conversation_cannot_upload_to_it_or_download_from_it()
    {
        var (ana, _, chloe, org, conversation) = await SetupAsync();
        var file = await ana.UploadAsync($"/api/conversations/{conversation.Id}/files", "notes.txt", Content);
        await ana.SendAsync(conversation.Id, null, "private", file.Id);

        await ResponseAssert.ProblemAsync(chloe.Client.GetAsync($"/api/files/{file.Id}/content"), 404, "not_found");
        await ResponseAssert.ProblemAsync(
            chloe.UploadRawAsync($"/api/conversations/{conversation.Id}/files", "x.txt", Content), 404, "not_found");
        Assert.Empty(await chloe.GetAsync<List<FileDto>>($"/api/orgs/{org.Id}/files"));
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var (ana, _, _, _, conversation) = await SetupAsync();

        var detail = await ResponseAssert.ProblemAsync(
            ana.UploadRawAsync($"/api/conversations/{conversation.Id}/files", "empty.txt", []), 400, "validation");

        Assert.Equal("The file is empty.", detail);
    }
}

[Collection(ApiCollection.Name)]
public class WorkspaceTests(ApiFactory api)
{
    [Fact]
    public async Task A_new_workspace_starts_with_its_creator_and_a_General_channel()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);

        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", "Planning"));

        Assert.Equal(WorkspaceRole.Owner, workspace.Role);
        Assert.Equal(1, workspace.MemberCount);
        Assert.Equal("General", Assert.Single(workspace.Channels).Name);
        Assert.Equal(workspace.Id, Assert.Single(await ana.GetAsync<List<WorkspaceDto>>($"/api/orgs/{org.Id}/workspaces")).Id);
        Assert.Empty(await ben.GetAsync<List<WorkspaceDto>>($"/api/orgs/{org.Id}/workspaces"));
    }

    [Fact]
    public async Task Only_owners_add_people_and_only_from_the_organization()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var (ana, ben, chloe) = (people[0], people[1], people[2]);
        var outsider = await api.NewUserAsync("Mallory");
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));
        var members = $"/api/workspaces/{workspace.Id}/members";

        // Not in the workspace: it does not exist as far as Ben can tell.
        await ResponseAssert.ProblemAsync(ben.PostAsync(members, new AddWorkspaceMemberRequest(ben.Id)), 404, "not_found");

        Assert.Equal(HttpStatusCode.NoContent, (await ana.PostAsync(members, new AddWorkspaceMemberRequest(ben.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.PostAsync(members, new AddWorkspaceMemberRequest(ben.Id))).StatusCode);

        // A member, but not an owner.
        await ResponseAssert.ProblemAsync(ben.PostAsync(members, new AddWorkspaceMemberRequest(chloe.Id)), 403, "forbidden");
        await ResponseAssert.ProblemAsync(ana.PostAsync(members, new AddWorkspaceMemberRequest(outsider.Id)), 400, "validation");

        var listed = await ben.GetAsync<List<WorkspaceMemberDto>>(members);
        Assert.Equal([("Ana", WorkspaceRole.Owner), ("Ben", WorkspaceRole.Member)], listed.Select(m => (m.DisplayName, m.Role)).Order());
        Assert.Equal(2, Assert.Single(await ben.GetAsync<List<WorkspaceDto>>($"/api/orgs/{org.Id}/workspaces")).MemberCount);
    }

    [Fact]
    public async Task Channel_names_are_unique_within_a_workspace_ignoring_case()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana");
        var ana = people[0];
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));
        var channels = $"/api/workspaces/{workspace.Id}/channels";

        var roadmap = await ana.PostAsync<ChannelDto>(channels, new CreateChannelRequest("Roadmap", null));
        var detail = await ResponseAssert.ProblemAsync(ana.PostAsync(channels, new CreateChannelRequest(" roadmap ", null)), 409, "conflict");
        await ResponseAssert.ProblemAsync(ana.PostAsync(channels, new CreateChannelRequest("general", null)), 409, "conflict");
        await ResponseAssert.ProblemAsync(ana.PostAsync(channels, new CreateChannelRequest(" ", null)), 400, "validation");

        Assert.Equal("Roadmap", roadmap.Name);
        Assert.Contains("roadmap", detail);

        // The same name is fine in another workspace.
        var other = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Engineering", null));
        await ana.PostAsync<ChannelDto>($"/api/workspaces/{other.Id}/channels", new CreateChannelRequest("Roadmap", null));
    }

    [Fact]
    public async Task Channel_posts_are_for_workspace_members_only()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var (ana, ben, chloe) = (people[0], people[1], people[2]);
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));
        await ana.PostAsync($"/api/workspaces/{workspace.Id}/members", new AddWorkspaceMemberRequest(ben.Id));
        var general = workspace.Channels[0];

        await ana.SendAsync(null, general.Id, "Welcome to Product");
        var reply = await ben.SendAsync(null, general.Id, "Glad to be here");

        Assert.Equal(general.Id, reply.ChannelId);
        var posts = await ben.GetAsync<List<MessageDto>>($"/api/channels/{general.Id}/messages");
        Assert.Equal(["Welcome to Product", "Glad to be here"], posts.Select(m => m.Body));

        await ResponseAssert.ProblemAsync(chloe.Client.GetAsync($"/api/channels/{general.Id}/messages"), 404, "not_found");
        await ResponseAssert.ProblemAsync(
            chloe.PostAsync("/api/messages", new SendMessageRequest(null, general.Id, "Let me in", null)), 404, "not_found");
        await ResponseAssert.ProblemAsync(
            chloe.PostAsync($"/api/workspaces/{workspace.Id}/channels", new CreateChannelRequest("Sneaky", null)), 404, "not_found");
    }

    [Fact]
    public async Task A_message_must_name_exactly_one_destination()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var ana = people[0];
        var conversation = await ana.PostAsync<ConversationDto>(
            $"/api/orgs/{org.Id}/conversations", new CreateConversationRequest([people[1].Id], null));
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));

        await ResponseAssert.ProblemAsync(
            ana.PostAsync("/api/messages", new SendMessageRequest(null, null, "nowhere", null)), 400, "validation");
        await ResponseAssert.ProblemAsync(
            ana.PostAsync("/api/messages", new SendMessageRequest(conversation.Id, workspace.Channels[0].Id, "both", null)), 400, "validation");
    }

    [Fact]
    public async Task Private_channels_are_only_visible_to_their_members()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var (ana, ben, chloe) = (people[0], people[1], people[2]);
        var workspace = await ana.PostAsync<WorkspaceDto>($"/api/orgs/{org.Id}/workspaces", new CreateWorkspaceRequest("Product", null));
        await ana.PostAsync($"/api/workspaces/{workspace.Id}/members", new AddWorkspaceMemberRequest(ben.Id));
        await ana.PostAsync($"/api/workspaces/{workspace.Id}/members", new AddWorkspaceMemberRequest(chloe.Id));

        var secret = await ana.PostAsync<ChannelDto>(
            $"/api/workspaces/{workspace.Id}/channels",
            new CreateChannelRequest("Secret", null, ChannelKind.Private, [ben.Id]));

        Assert.Equal(ChannelKind.Private, secret.Kind);
        Assert.Equal(2, secret.MemberCount);

        var anaChannels = Assert.Single(await ana.GetAsync<List<WorkspaceDto>>($"/api/orgs/{org.Id}/workspaces")).Channels;
        var benChannels = Assert.Single(await ben.GetAsync<List<WorkspaceDto>>($"/api/orgs/{org.Id}/workspaces")).Channels;
        var chloeChannels = Assert.Single(await chloe.GetAsync<List<WorkspaceDto>>($"/api/orgs/{org.Id}/workspaces")).Channels;
        Assert.Contains(anaChannels, c => c.Id == secret.Id);
        Assert.Contains(benChannels, c => c.Id == secret.Id);
        Assert.DoesNotContain(chloeChannels, c => c.Id == secret.Id);

        await ana.SendAsync(null, secret.Id, "heads only");
        Assert.Equal("heads only", Assert.Single(await ben.GetAsync<List<MessageDto>>($"/api/channels/{secret.Id}/messages")).Body);
        await ResponseAssert.ProblemAsync(chloe.Client.GetAsync($"/api/channels/{secret.Id}/messages"), 404, "not_found");
        await ResponseAssert.ProblemAsync(
            chloe.PostAsync("/api/messages", new SendMessageRequest(null, secret.Id, "sneak", null)), 404, "not_found");
    }
}

[Collection(ApiCollection.Name)]
public class SearchTests(ApiFactory api)
{
    [Fact]
    public async Task Search_finds_people_and_messages_the_caller_can_see()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana Reyes", "Ben Okafor");
        var (ana, ben) = (people[0], people[1]);
        var conversation = await ana.PostAsync<ConversationDto>(
            $"/api/orgs/{org.Id}/conversations", new CreateConversationRequest([ben.Id], null));
        await ana.SendAsync(conversation.Id, null, "Ship the release notes tomorrow");

        var results = await ana.GetAsync<SearchResultDto>($"/api/orgs/{org.Id}/search?q=release");
        Assert.Contains(results.Messages, m => m.BodyPreview.Contains("release", StringComparison.OrdinalIgnoreCase));

        var peopleHits = await ana.GetAsync<SearchResultDto>($"/api/orgs/{org.Id}/search?q=Ben");
        Assert.Contains(peopleHits.People, p => p.Id == ben.Id);
    }
}

[Collection(ApiCollection.Name)]
public class MeetingTests(ApiFactory api)
{
    [Fact]
    public async Task Joining_returns_a_media_token_for_that_person_and_that_room_only()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var meeting = await ana.PostAsync<MeetingDto>($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("Standup"));

        var join = await ben.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");

        Assert.Equal("ws://media.test:7880", join.ServerUrl);
        Assert.StartsWith("mj-", join.RoomName);
        var claims = DecodeJwtPayload(join.Token);
        Assert.Equal(ben.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal("test_key", claims.GetProperty("iss").GetString());
        var grant = claims.GetProperty("video");
        Assert.Equal(join.RoomName, grant.GetProperty("room").GetString());
        Assert.True(grant.GetProperty("roomJoin").GetBoolean());
        Assert.False(grant.TryGetProperty("roomAdmin", out var admin) && admin.GetBoolean());
        Assert.False(grant.TryGetProperty("roomCreate", out var create) && create.GetBoolean());
    }

    [Fact]
    public async Task The_list_shows_active_meetings_with_who_is_in_them()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var meeting = await ana.PostAsync<MeetingDto>($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("Standup"));
        Assert.Empty(meeting.Participants);

        await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        await ben.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        await ben.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join"); // Rejoining does not duplicate.

        var listed = Assert.Single(await ben.GetAsync<List<MeetingDto>>($"/api/orgs/{org.Id}/meetings"));
        Assert.Equal(["Ana", "Ben"], listed.Participants.Select(p => p.DisplayName).Order());
        Assert.Equal(ana.Id, listed.Host.Id);
    }

    [Fact]
    public async Task Only_the_host_can_end_a_meeting_and_then_nobody_can_join()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var meeting = await ana.PostAsync<MeetingDto>($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("Standup"));
        await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        await ben.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");

        await ResponseAssert.ProblemAsync(ben.PostAsync($"/api/meetings/{meeting.Id}/end"), 403, "forbidden");
        Assert.Equal(HttpStatusCode.NoContent, (await ana.PostAsync($"/api/meetings/{meeting.Id}/end")).StatusCode);

        Assert.Empty(await ben.GetAsync<List<MeetingDto>>($"/api/orgs/{org.Id}/meetings"));
        var detail = await ResponseAssert.ProblemAsync(ben.PostAsync($"/api/meetings/{meeting.Id}/join"), 409, "conflict");
        Assert.Equal("This meeting has ended.", detail);
    }

    [Fact]
    public async Task A_meeting_ends_when_the_last_person_leaves()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var meeting = await ana.PostAsync<MeetingDto>($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("Standup"));
        await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        await ben.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");

        await ana.PostAsync($"/api/meetings/{meeting.Id}/leave");
        Assert.Equal("Ben", Assert.Single(Assert.Single(await ben.GetAsync<List<MeetingDto>>($"/api/orgs/{org.Id}/meetings")).Participants).DisplayName);

        await ben.PostAsync($"/api/meetings/{meeting.Id}/leave");
        Assert.Empty(await ana.GetAsync<List<MeetingDto>>($"/api/orgs/{org.Id}/meetings"));
    }

    [Fact]
    public async Task People_outside_the_organization_cannot_join_or_end_its_meetings()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana");
        var outsider = await api.NewUserAsync("Mallory");
        var meeting = await people[0].PostAsync<MeetingDto>($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("Board"));

        await ResponseAssert.ProblemAsync(outsider.PostAsync($"/api/meetings/{meeting.Id}/join"), 404, "not_found");
        await ResponseAssert.ProblemAsync(outsider.PostAsync($"/api/meetings/{meeting.Id}/end"), 404, "not_found");
        await ResponseAssert.ProblemAsync(outsider.PostAsync($"/api/meetings/{meeting.Id}/leave"), 404, "not_found");
    }

    [Fact]
    public async Task A_meeting_needs_a_title()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana");

        await ResponseAssert.ProblemAsync(
            people[0].PostAsync($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("  ")), 400, "validation");
    }

    [Fact]
    public async Task Scheduling_creates_invites_and_calendar_entries()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben");
        var (ana, ben) = (people[0], people[1]);
        var starts = DateTimeOffset.UtcNow.AddDays(2).AddHours(3);

        var meeting = await ana.PostAsync<MeetingDto>(
            $"/api/orgs/{org.Id}/meetings/schedule",
            new ScheduleMeetingRequest("Planning", "Agenda", starts, starts.AddHours(1), true, [ben.Id]));

        Assert.True(meeting.IsScheduled);
        Assert.True(meeting.LobbyEnabled);
        Assert.Equal(InviteStatus.Pending, Assert.Single(meeting.Invites).Status);
        Assert.Equal(ben.Id, meeting.Invites[0].UserId);

        var calendar = await ben.GetAsync<List<MeetingDto>>(
            $"/api/orgs/{org.Id}/calendar?from={Uri.EscapeDataString(starts.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(starts.AddDays(1).ToString("O"))}");
        Assert.Contains(calendar, m => m.Id == meeting.Id);

        var accepted = await ben.PostAsync<MeetingDto>(
            $"/api/meetings/{meeting.Id}/invite", new RespondInviteRequest(InviteStatus.Accepted));
        Assert.Equal(InviteStatus.Accepted, Assert.Single(accepted.Invites).Status);

        var hostJoin = await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        Assert.False(hostJoin.InLobby);
        var hostClaims = DecodeJwtPayload(hostJoin.Token).GetProperty("video");
        Assert.True(hostClaims.GetProperty("roomAdmin").GetBoolean());

        var lobbyJoin = await ben.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        Assert.True(lobbyJoin.InLobby);
        Assert.False(DecodeJwtPayload(lobbyJoin.Token).GetProperty("video").GetProperty("canPublish").GetBoolean());

        await using var hub = await ana.ConnectAsync(HubPaths.Meeting);
        await hub.InvokeAsync(MeetingHubMethods.Admit, meeting.Id, ben.Id);

        var admitted = await ben.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");
        Assert.False(admitted.InLobby);
        Assert.True(DecodeJwtPayload(admitted.Token).GetProperty("video").GetProperty("canPublish").GetBoolean());

        var roster = await ana.GetAsync<List<MeetingDto>>($"/api/orgs/{org.Id}/meetings");
        var live = Assert.Single(roster, m => m.Id == meeting.Id);
        Assert.Contains(live.Participants, p => p.Id == ben.Id);
        Assert.DoesNotContain(live.Lobby, p => p.Id == ben.Id);
    }

    [Fact]
    public async Task Email_guest_session_gets_publishable_token_after_host_admits()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana");
        var ana = people[0];
        var starts = DateTimeOffset.UtcNow.AddMinutes(-5);
        var meeting = await ana.PostAsync<MeetingDto>(
            $"/api/orgs/{org.Id}/meetings/schedule",
            new ScheduleMeetingRequest(
                "EOG Demo", "Agenda", starts, starts.AddHours(1), true, null, RecurrenceKind.None, null,
                ["paul@example.com"]));

        await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");

        string inviteToken;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            inviteToken = await db.MeetingGuestInvites
                .Where(i => i.MeetingId == meeting.Id)
                .Select(i => i.InviteToken)
                .SingleAsync();
        }

        var guestClient = api.CreateClient();
        var joinResponse = await guestClient.PostAsJsonAsync(
            $"/api/public/meeting-invites/{Uri.EscapeDataString(inviteToken)}/join",
            new GuestJoinRequest("Paul"),
            ApiFactory.Json);
        joinResponse.EnsureSuccessStatusCode();
        var lobbyJoin = (await joinResponse.Content.ReadFromJsonAsync<GuestJoinDto>(ApiFactory.Json))!;
        Assert.True(lobbyJoin.InLobby);
        Assert.True(lobbyJoin.IsPresent);
        Assert.False(DecodeJwtPayload(lobbyJoin.Token).GetProperty("video").GetProperty("canPublish").GetBoolean());

        await using var hub = await ana.ConnectAsync(HubPaths.Meeting);
        await hub.InvokeAsync(MeetingHubMethods.Admit, meeting.Id, lobbyJoin.GuestUserId);

        var sessionResponse = await guestClient.GetAsync(
            $"/api/public/meeting-invites/{Uri.EscapeDataString(inviteToken)}/session");
        sessionResponse.EnsureSuccessStatusCode();
        var session = (await sessionResponse.Content.ReadFromJsonAsync<GuestJoinDto>(ApiFactory.Json))!;
        Assert.True(session.IsPresent);
        Assert.False(session.InLobby);
        Assert.False(string.IsNullOrWhiteSpace(session.ServerUrl));
        Assert.False(string.IsNullOrWhiteSpace(session.Token));
        Assert.True(DecodeJwtPayload(session.Token).GetProperty("video").GetProperty("canPublish").GetBoolean());
    }

    [Fact]
    public async Task Email_guest_leave_removes_them_from_the_people_list()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana");
        var ana = people[0];
        var starts = DateTimeOffset.UtcNow.AddMinutes(-5);
        var meeting = await ana.PostAsync<MeetingDto>(
            $"/api/orgs/{org.Id}/meetings/schedule",
            new ScheduleMeetingRequest(
                "EOG Demo", "Agenda", starts, starts.AddHours(1), false, null, RecurrenceKind.None, null,
                ["paul@example.com"]));

        await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");

        string inviteToken;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            inviteToken = await db.MeetingGuestInvites
                .Where(i => i.MeetingId == meeting.Id)
                .Select(i => i.InviteToken)
                .SingleAsync();
        }

        var guestClient = api.CreateClient();
        var joinResponse = await guestClient.PostAsJsonAsync(
            $"/api/public/meeting-invites/{Uri.EscapeDataString(inviteToken)}/join",
            new GuestJoinRequest("Paul"),
            ApiFactory.Json);
        joinResponse.EnsureSuccessStatusCode();
        var guestJoin = (await joinResponse.Content.ReadFromJsonAsync<GuestJoinDto>(ApiFactory.Json))!;
        Assert.False(guestJoin.InLobby);

        var withGuest = Assert.Single(await ana.GetAsync<List<MeetingDto>>($"/api/orgs/{org.Id}/meetings"));
        Assert.Contains(withGuest.Participants, p => p.Id == guestJoin.GuestUserId);

        var leaveResponse = await guestClient.PostAsync(
            $"/api/public/meeting-invites/{Uri.EscapeDataString(inviteToken)}/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, leaveResponse.StatusCode);

        var afterLeave = Assert.Single(await ana.GetAsync<List<MeetingDto>>($"/api/orgs/{org.Id}/meetings"));
        Assert.DoesNotContain(afterLeave.Participants, p => p.Id == guestJoin.GuestUserId);
        Assert.Contains(afterLeave.Participants, p => p.Id == ana.Id);
    }

    [Fact]
    public async Task Scheduling_accepts_local_offset_datetimes_and_stores_utc()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana");
        var ana = people[0];
        // Desktop clients send wall-clock times with a local offset (e.g. PH +08:00).
        // Npgsql rejects non-UTC DateTimeOffset for timestamptz unless we normalize first.
        var starts = new DateTimeOffset(2026, 11, 26, 20, 12, 0, TimeSpan.FromHours(8));

        var meeting = await ana.PostAsync<MeetingDto>(
            $"/api/orgs/{org.Id}/meetings/schedule",
            new ScheduleMeetingRequest(
                "Client Demo", "EOG", starts, starts.AddHours(1), true, null, RecurrenceKind.None, null,
                ["guest@example.com"]));

        Assert.Equal(TimeSpan.Zero, meeting.StartsAt.Offset);
        Assert.Equal(starts.ToUniversalTime(), meeting.StartsAt);
        Assert.True(meeting.IsScheduled);
    }

    [Fact]
    public async Task Participants_can_invite_org_members_into_a_live_meeting()
    {
        var (org, people) = await api.NewOrganizationAsync("Ana", "Ben", "Chloe");
        var (ana, ben, chloe) = (people[0], people[1], people[2]);
        var meeting = await ana.PostAsync<MeetingDto>($"/api/orgs/{org.Id}/meetings", new CreateMeetingRequest("Standup"));
        await ana.PostAsync<MeetingJoinDto>($"/api/meetings/{meeting.Id}/join");

        var invited = await ana.PostAsync<MeetingDto>(
            $"/api/meetings/{meeting.Id}/participants", new InviteParticipantsRequest([ben.Id, chloe.Id]));

        Assert.Equal(2, invited.Invites.Count);
        Assert.Contains(invited.Invites, i => i.UserId == ben.Id && i.Status == InviteStatus.Pending);
        Assert.Contains(invited.Invites, i => i.UserId == chloe.Id && i.Status == InviteStatus.Pending);

        await ResponseAssert.ProblemAsync(
            ana.PostAsync($"/api/meetings/{meeting.Id}/participants", new InviteParticipantsRequest([])),
            400, "validation");
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }
}
