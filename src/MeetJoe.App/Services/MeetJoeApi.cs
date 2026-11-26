using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetJoe.Contracts;

namespace MeetJoe.App.Services;

/// <summary>Adds the bearer token to every request.</summary>
public sealed class AuthHeaderHandler(IAuthService auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (await auth.GetAccessTokenAsync() is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}

public sealed class MeetJoeApi(HttpClient http, IAuthService auth) : IMeetJoeApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/auth/register", request, ct);

    public Task<MeDto> GetMeAsync(CancellationToken ct = default) => GetAsync<MeDto>("api/me", ct);

    public Task<MeDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default) =>
        SendAsync<MeDto>(HttpMethod.Put, "api/me", request, ct);

    public async Task<MeDto> UploadAvatarAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var contentType = FileTypes.ResolveContentType(fileName, buffer);
        buffer.Position = 0;

        using var form = new MultipartFormDataContent();
        var file = new StreamContent(buffer);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        using var response = await http.PostAsync("api/me/avatar", form, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<MeDto>(response, ct);
    }

    public Task<MeDto> ClearAvatarAsync(CancellationToken ct = default) =>
        SendAsync<MeDto>(HttpMethod.Delete, "api/me/avatar", null, ct);

    public async Task<Stream> DownloadAvatarAsync(Guid userId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            $"api/users/{userId:D}/avatar", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var copy = new MemoryStream();
        await response.Content.CopyToAsync(copy, ct);
        copy.Position = 0;
        return copy;
    }

    public Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<OrganizationDto>>("api/orgs", ct);

    public Task<OrganizationDto> CreateOrganizationAsync(string name, CancellationToken ct = default) =>
        SendAsync<OrganizationDto>(HttpMethod.Post, "api/orgs", new CreateOrganizationRequest(name), ct);

    public Task<OrganizationDto> JoinOrganizationAsync(string joinCode, CancellationToken ct = default) =>
        SendAsync<OrganizationDto>(HttpMethod.Post, "api/orgs/join", new JoinOrganizationRequest(joinCode), ct);

    public Task LeaveOrganizationAsync(Guid organizationId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/orgs/{organizationId}/leave", null, ct);

    public Task<IReadOnlyList<OrgMemberDto>> GetMembersAsync(Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<OrgMemberDto>>($"api/orgs/{organizationId}/members", ct);

    public Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ConversationDto>>($"api/orgs/{organizationId}/conversations", ct);

    public Task<ConversationDto> CreateConversationAsync(
        Guid organizationId, CreateConversationRequest request, CancellationToken ct = default) =>
        SendAsync<ConversationDto>(HttpMethod.Post, $"api/orgs/{organizationId}/conversations", request, ct);

    public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        Guid? conversationId, Guid? channelId, DateTimeOffset? before, CancellationToken ct = default)
    {
        var path = conversationId is { } id ? $"api/conversations/{id}/messages" : $"api/channels/{channelId}/messages";
        if (before is { } cutoff) path += $"?before={Uri.EscapeDataString(cutoff.ToUniversalTime().ToString("O"))}";
        return GetAsync<IReadOnlyList<MessageDto>>(path, ct);
    }

    public Task<IReadOnlyList<MessageDto>> GetRepliesAsync(Guid parentMessageId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MessageDto>>($"api/messages/{parentMessageId}/replies", ct);

    public Task MarkConversationReadAsync(Guid conversationId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/conversations/{conversationId}/read", null, ct);

    public Task MarkChannelReadAsync(Guid channelId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/channels/{channelId}/read", null, ct);

    public Task<IReadOnlyList<ReadCursorDto>> GetChannelReadCursorsAsync(Guid channelId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ReadCursorDto>>($"api/channels/{channelId}/read-cursors", ct);

    public Task MarkMeetingChatReadAsync(Guid meetingId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/meetings/{meetingId}/messages/read", null, ct);

    public Task<ConversationDto> AddConversationMemberAsync(
        Guid conversationId, Guid userId, CancellationToken ct = default) =>
        SendAsync<ConversationDto>(
            HttpMethod.Post, $"api/conversations/{conversationId}/members",
            new AddConversationMemberRequest(userId), ct);

    public async Task<ConversationDto?> RemoveConversationMemberAsync(
        Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(
            HttpMethod.Delete, $"api/conversations/{conversationId}/members/{userId:D}", null, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;
        return await ReadAsync<ConversationDto>(response, ct);
    }

    public Task<ConversationDto> RenameConversationAsync(
        Guid conversationId, string title, CancellationToken ct = default) =>
        SendAsync<ConversationDto>(
            HttpMethod.Put, $"api/conversations/{conversationId}/title",
            new RenameConversationRequest(title), ct);

    public Task<ConversationDto> UpdateConversationPrefsAsync(
        Guid conversationId, UpdateConversationPrefsRequest request, CancellationToken ct = default) =>
        SendAsync<ConversationDto>(HttpMethod.Put, $"api/conversations/{conversationId}/prefs", request, ct);

    public Task<MessageDto> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default) =>
        SendAsync<MessageDto>(HttpMethod.Post, "api/messages", request, ct);

    public Task<MessageDto> EditMessageAsync(Guid messageId, string body, CancellationToken ct = default) =>
        SendAsync<MessageDto>(HttpMethod.Put, $"api/messages/{messageId}", new EditMessageRequest(body), ct);

    public Task<MessageDto> DeleteMessageAsync(Guid messageId, CancellationToken ct = default) =>
        SendAsync<MessageDto>(HttpMethod.Delete, $"api/messages/{messageId}", null, ct);

    public Task<MessageDto> ToggleReactionAsync(Guid messageId, string emoji, CancellationToken ct = default) =>
        SendAsync<MessageDto>(HttpMethod.Post, $"api/messages/{messageId}/reactions", new ToggleReactionRequest(emoji), ct);

    public Task<IReadOnlyList<ActivityDto>> GetActivityAsync(Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ActivityDto>>($"api/orgs/{organizationId}/activity", ct);

    public Task MarkActivityReadAsync(Guid activityId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/activity/{activityId}/read", null, ct);

    public Task MarkAllActivityReadAsync(Guid organizationId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/orgs/{organizationId}/activity/read", null, ct);

    public Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkspaceDto>>($"api/orgs/{organizationId}/workspaces", ct);

    public Task<WorkspaceDto> CreateWorkspaceAsync(
        Guid organizationId, CreateWorkspaceRequest request, CancellationToken ct = default) =>
        SendAsync<WorkspaceDto>(HttpMethod.Post, $"api/orgs/{organizationId}/workspaces", request, ct);

    public Task<IReadOnlyList<WorkspaceMemberDto>> GetWorkspaceMembersAsync(Guid workspaceId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkspaceMemberDto>>($"api/workspaces/{workspaceId}/members", ct);

    public Task AddWorkspaceMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/workspaces/{workspaceId}/members", new AddWorkspaceMemberRequest(userId), ct);

    public Task<ChannelDto> CreateChannelAsync(Guid workspaceId, CreateChannelRequest request, CancellationToken ct = default) =>
        SendAsync<ChannelDto>(HttpMethod.Post, $"api/workspaces/{workspaceId}/channels", request, ct);

    public Task<IReadOnlyList<ChannelMemberDto>> GetChannelMembersAsync(Guid channelId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ChannelMemberDto>>($"api/channels/{channelId}/members", ct);

    public Task AddChannelMemberAsync(Guid channelId, Guid userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/channels/{channelId}/members", new AddChannelMemberRequest(userId), ct);

    public Task RemoveChannelMemberAsync(Guid channelId, Guid userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/channels/{channelId}/members/{userId}", null, ct);

    public Task<ChannelDto> UpdateChannelPrefsAsync(Guid channelId, UpdateChannelPrefsRequest request, CancellationToken ct = default) =>
        SendAsync<ChannelDto>(HttpMethod.Put, $"api/channels/{channelId}/prefs", request, ct);

    public Task<SearchResultDto> SearchAsync(Guid organizationId, string query, CancellationToken ct = default) =>
        GetAsync<SearchResultDto>($"api/orgs/{organizationId}/search?q={Uri.EscapeDataString(query)}", ct);

    public Task<IReadOnlyList<FileDto>> GetFilesAsync(Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FileDto>>($"api/orgs/{organizationId}/files", ct);

    public async Task<FileDto> UploadFileAsync(
        Guid? conversationId,
        Guid? channelId,
        string fileName,
        Stream content,
        CancellationToken ct = default,
        Guid? meetingId = null)
    {
        var path = meetingId is { } mid
            ? $"api/meetings/{mid}/files"
            : conversationId is { } id
                ? $"api/conversations/{id}/files"
                : $"api/channels/{channelId}/files";

        // Buffer so we can sniff magic bytes (Photos often supplies a GUID name with no extension).
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var contentType = FileTypes.ResolveContentType(fileName, buffer);
        buffer.Position = 0;

        using var form = new MultipartFormDataContent();
        var file = new StreamContent(buffer);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        using var response = await http.PostAsync(path, form, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<FileDto>(response, ct);
    }

    public async Task DownloadFileAsync(Guid fileId, Stream destination, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            $"api/files/{fileId}/content", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        await response.Content.CopyToAsync(destination, ct);
    }

    public Task<IReadOnlyList<MeetingDto>> GetMeetingsAsync(Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MeetingDto>>($"api/orgs/{organizationId}/meetings", ct);

    public Task<IReadOnlyList<MeetingDto>> GetCalendarAsync(
        Guid organizationId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MeetingDto>>(
            $"api/orgs/{organizationId}/calendar?from={Uri.EscapeDataString(from.ToUniversalTime().ToString("O"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("O"))}",
            ct);

    public Task<MeetingDto> CreateMeetingAsync(Guid organizationId, string title, CancellationToken ct = default) =>
        SendAsync<MeetingDto>(HttpMethod.Post, $"api/orgs/{organizationId}/meetings", new CreateMeetingRequest(title), ct);

    public Task<MeetingDto> ScheduleMeetingAsync(
        Guid organizationId, ScheduleMeetingRequest request, CancellationToken ct = default) =>
        SendAsync<MeetingDto>(HttpMethod.Post, $"api/orgs/{organizationId}/meetings/schedule", request, ct);

    public Task<GuestMeetingInviteDto> GetGuestMeetingInviteAsync(string token, CancellationToken ct = default) =>
        GetAsync<GuestMeetingInviteDto>($"api/public/meeting-invites/{Uri.EscapeDataString(token)}", ct);

    public Task<GuestJoinDto> JoinGuestMeetingInviteAsync(
        string token, string displayName, CancellationToken ct = default) =>
        SendAsync<GuestJoinDto>(
            HttpMethod.Post, $"api/public/meeting-invites/{Uri.EscapeDataString(token)}/join",
            new GuestJoinRequest(displayName), ct);

    public Task<MeetingJoinDto> JoinMeetingAsync(Guid meetingId, CancellationToken ct = default) =>
        SendAsync<MeetingJoinDto>(HttpMethod.Post, $"api/meetings/{meetingId}/join", null, ct);

    public Task LeaveMeetingAsync(Guid meetingId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/meetings/{meetingId}/leave", null, ct);

    public Task EndMeetingAsync(Guid meetingId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/meetings/{meetingId}/end", null, ct);

    public Task<MeetingDto> RespondToInviteAsync(Guid meetingId, InviteStatus status, CancellationToken ct = default) =>
        SendAsync<MeetingDto>(HttpMethod.Post, $"api/meetings/{meetingId}/invite", new RespondInviteRequest(status), ct);

    public Task<MeetingDto> InviteParticipantsAsync(
        Guid meetingId, IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        SendAsync<MeetingDto>(HttpMethod.Post, $"api/meetings/{meetingId}/participants",
            new InviteParticipantsRequest(userIds), ct);

    public Task<IReadOnlyList<MessageDto>> GetMeetingMessagesAsync(Guid meetingId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MessageDto>>($"api/meetings/{meetingId}/messages", ct);

    public Task<MessageDto> SendMeetingChatAsync(
        Guid meetingId, string? body, CancellationToken ct = default, IReadOnlyList<Guid>? attachmentIds = null) =>
        SendAsync<MessageDto>(HttpMethod.Post, $"api/meetings/{meetingId}/messages",
            new SendMessageRequest(null, null, body, attachmentIds, null, meetingId), ct);

    public Task<MeetingRecordingDto> StartRecordingAsync(Guid meetingId, CancellationToken ct = default) =>
        SendAsync<MeetingRecordingDto>(HttpMethod.Post, $"api/meetings/{meetingId}/recording", null, ct);

    public Task<MeetingRecordingDto> CompleteRecordingAsync(
        Guid meetingId, Guid fileId, CancellationToken ct = default) =>
        SendAsync<MeetingRecordingDto>(
            HttpMethod.Post, $"api/meetings/{meetingId}/recording/complete",
            new CompleteRecordingRequest(fileId), ct);

    public Task CancelRecordingAsync(Guid meetingId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/meetings/{meetingId}/recording/cancel", null, ct);

    public Task<string?> GetAccessTokenAsync() => auth.GetAccessTokenAsync();

    public Task<IReadOnlyList<CallDto>> GetCallsAsync(Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CallDto>>($"api/orgs/{organizationId}/calls", ct);

    public Task<CallJoinDto> StartCallAsync(Guid organizationId, Guid conversationId, CancellationToken ct = default) =>
        SendAsync<CallJoinDto>(HttpMethod.Post, $"api/orgs/{organizationId}/calls", new StartCallRequest(conversationId), ct);

    public Task<CallJoinDto> AnswerCallAsync(Guid callId, CancellationToken ct = default) =>
        SendAsync<CallJoinDto>(HttpMethod.Post, $"api/calls/{callId}/answer", null, ct);

    public Task DeclineCallAsync(Guid callId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/calls/{callId}/decline", null, ct);

    public Task HangUpCallAsync(Guid callId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/calls/{callId}/hangup", null, ct);

    public Task EndCallAsync(Guid callId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/calls/{callId}/end", null, ct);

    public Task<CallDto> InviteToCallAsync(Guid callId, IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        SendAsync<CallDto>(HttpMethod.Post, $"api/calls/{callId}/invite", new InviteToCallRequest(userIds), ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendCoreAsync(method, path, body, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendCoreAsync(method, path, body, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return http.SendAsync(request, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct)
        ?? throw new ApiException("The server returned an empty response.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string? detail = null;
        try
        {
            detail = (await response.Content.ReadFromJsonAsync<ApiProblem>(Json, ct))?.Detail;
        }
        catch (JsonException)
        {
            // Not a problem-details body; fall through to the generic message.
        }
        catch (NotSupportedException)
        {
            // Not JSON at all.
        }

        var status = (int)response.StatusCode;
        throw new ApiException(
            detail ?? status switch
            {
                401 => "Your session has ended. Sign in again.",
                413 => "That file is too large.",
                429 => "Too many attempts. Wait a minute and try again.",
                >= 500 => "The server ran into a problem. Try again shortly.",
                _ => $"The request failed ({status}).",
            },
            status);
    }
}
