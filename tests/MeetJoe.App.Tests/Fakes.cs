using MeetJoe.App.Services;
using MeetJoe.App.ViewModels;
using MeetJoe.Contracts;

namespace MeetJoe.App.Tests;

/// <summary>An in-memory stand-in for the MeetJoe server, as seen by one signed-in user.</summary>
internal sealed class FakeApi : IMeetJoeApi
{
    public MeDto Me { get; set; } = new(Guid.NewGuid(), "ana@example.com", "Ana Reyes", PresenceStatus.Available, null);
    public List<OrganizationDto> Organizations { get; } = [];
    public List<OrgMemberDto> Members { get; } = [];
    public List<ConversationDto> Conversations { get; } = [];
    public Dictionary<Guid, List<MessageDto>> Messages { get; } = [];
    public Dictionary<Guid, List<MessageDto>> Replies { get; } = [];
    public List<ActivityDto> Activity { get; } = [];
    public List<WorkspaceDto> Workspaces { get; } = [];
    public Dictionary<Guid, List<WorkspaceMemberDto>> WorkspaceMembers { get; } = [];
    public List<FileDto> Files { get; } = [];
    public List<MeetingDto> Meetings { get; } = [];
    public List<CallDto> Calls { get; } = [];

    public List<Guid> MarkedRead { get; } = [];
    public List<SendMessageRequest> Sent { get; } = [];
    public List<RegisterRequest> Registered { get; } = [];

    /// <summary>When set, the next send fails with this.</summary>
    public Exception? SendFailure { get; set; }

    public UserSummaryDto MeSummary => new(Me.Id, Me.DisplayName);

    public MessageDto NewMessage(Guid? conversationId, Guid? channelId, UserSummaryDto sender, string body, DateTimeOffset? at = null)
    {
        var message = new MessageDto(Guid.NewGuid(), conversationId, channelId, sender, body, at ?? DateTimeOffset.Now, []);
        var key = conversationId ?? channelId!.Value;
        if (!Messages.TryGetValue(key, out var list)) Messages[key] = list = [];
        list.Add(message);
        return message;
    }

    public Task RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        Registered.Add(request);
        return Task.CompletedTask;
    }

    public Task<MeDto> GetMeAsync(CancellationToken ct = default) => Task.FromResult(Me);

    public Task<MeDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default)
    {
        Me = Me with { DisplayName = request.DisplayName, StatusMessage = request.StatusMessage };
        return Task.FromResult(Me);
    }

    public Task<MeDto> UploadAvatarAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        Me = Me with { AvatarUrl = $"api/users/{Me.Id:D}/avatar" };
        return Task.FromResult(Me);
    }

    public Task<MeDto> ClearAvatarAsync(CancellationToken ct = default)
    {
        Me = Me with { AvatarUrl = null };
        return Task.FromResult(Me);
    }

    public Task<Stream> DownloadAvatarAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrganizationDto>>(Organizations.ToList());

    public Task<OrganizationDto> CreateOrganizationAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ApiException("Enter a name for the organization.", 400);
        var organization = new OrganizationDto(Guid.NewGuid(), name, OrgRole.Owner, "ABCDE-FGHJK");
        Organizations.Add(organization);
        return Task.FromResult(organization);
    }

    public Task<OrganizationDto> JoinOrganizationAsync(string joinCode, CancellationToken ct = default) =>
        throw new ApiException("That invite code is not valid.", 400);

    public Task LeaveOrganizationAsync(Guid organizationId, CancellationToken ct = default)
    {
        Organizations.RemoveAll(o => o.Id == organizationId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OrgMemberDto>> GetMembersAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgMemberDto>>(Members.ToList());

    public Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationDto>>(Conversations.ToList());

    public Task<ConversationDto> CreateConversationAsync(
        Guid organizationId, CreateConversationRequest request, CancellationToken ct = default)
    {
        var members = request.MemberIds
            .Select(id => new UserSummaryDto(id, Members.First(m => m.UserId == id).DisplayName))
            .Prepend(MeSummary)
            .ToList();
        var conversation = new ConversationDto(
            Guid.NewGuid(), members.Count > 2 ? ConversationKind.Group : ConversationKind.Direct,
            request.Title, members, null, null, 0);
        Conversations.Insert(0, conversation);
        return Task.FromResult(conversation);
    }

    public Task<ConversationDto> AddConversationMemberAsync(
        Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var index = Conversations.FindIndex(c => c.Id == conversationId);
        if (index < 0) throw new ApiException("Conversation was not found.", 404);
        var current = Conversations[index];
        if (current.Kind != ConversationKind.Group)
            throw new ApiException("Only group conversations can change membership.", 400);
        if (current.Members.Any(m => m.Id == userId))
            throw new ApiException("That person is already in this conversation.", 400);
        var person = Members.First(m => m.UserId == userId);
        var updated = current with
        {
            Members = current.Members.Append(new UserSummaryDto(userId, person.DisplayName)).ToList(),
        };
        Conversations[index] = updated;
        return Task.FromResult(updated);
    }

    public Task<ConversationDto?> RemoveConversationMemberAsync(
        Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var index = Conversations.FindIndex(c => c.Id == conversationId);
        if (index < 0) throw new ApiException("Conversation was not found.", 404);
        var current = Conversations[index];
        if (current.Kind != ConversationKind.Group)
            throw new ApiException("Only group conversations can change membership.", 400);
        var next = current.Members.Where(m => m.Id != userId).ToList();
        if (next.Count == current.Members.Count)
            throw new ApiException("That person is not in this conversation.", 400);
        if (next.Count == 0)
        {
            Conversations.RemoveAt(index);
            return Task.FromResult<ConversationDto?>(null);
        }

        var updated = current with { Members = next };
        Conversations[index] = updated;
        return Task.FromResult<ConversationDto?>(updated);
    }

    public Task<ConversationDto> RenameConversationAsync(
        Guid conversationId, string title, CancellationToken ct = default)
    {
        var index = Conversations.FindIndex(c => c.Id == conversationId);
        if (index < 0) throw new ApiException("Conversation was not found.", 404);
        var current = Conversations[index];
        if (current.Kind != ConversationKind.Group)
            throw new ApiException("Only group conversations can be renamed.", 400);
        if (current.CreatedById is not { } creator || creator != Me.Id)
            throw new ApiException("Only the person who created this group can rename it.", 400);
        var updated = current with { Title = title.Trim() };
        Conversations[index] = updated;
        return Task.FromResult(updated);
    }

    public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        Guid? conversationId, Guid? channelId, DateTimeOffset? before, CancellationToken ct = default)
    {
        var key = conversationId ?? channelId!.Value;
        var all = Messages.TryGetValue(key, out var list) ? list : [];
        var page = all.Where(m => m.ParentMessageId is null && (before is null || m.CreatedAt < before)).TakeLast(50).ToList();
        return Task.FromResult<IReadOnlyList<MessageDto>>(page);
    }

    public Task<IReadOnlyList<MessageDto>> GetRepliesAsync(Guid parentMessageId, CancellationToken ct = default)
    {
        var page = Replies.TryGetValue(parentMessageId, out var list) ? list : [];
        return Task.FromResult<IReadOnlyList<MessageDto>>(page.ToList());
    }

    public Task MarkConversationReadAsync(Guid conversationId, CancellationToken ct = default)
    {
        MarkedRead.Add(conversationId);
        return Task.CompletedTask;
    }

    public Task MarkChannelReadAsync(Guid channelId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ReadCursorDto>> GetChannelReadCursorsAsync(Guid channelId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReadCursorDto>>([]);

    public Task MarkMeetingChatReadAsync(Guid meetingId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ConversationDto> UpdateConversationPrefsAsync(
        Guid conversationId, UpdateConversationPrefsRequest request, CancellationToken ct = default)
    {
        var index = Conversations.FindIndex(c => c.Id == conversationId);
        if (index < 0) throw new ApiException("Conversation was not found.", 404);
        Conversations[index] = Conversations[index] with
        {
            IsMuted = request.IsMuted,
            IsPinned = request.IsPinned,
            NotifyMode = request.NotifyMode,
        };
        return Task.FromResult(Conversations[index]);
    }

    public Task<MessageDto> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default)
    {
        if (SendFailure is { } failure)
        {
            SendFailure = null;
            throw failure;
        }

        Sent.Add(request);
        var message = new MessageDto(
            Guid.NewGuid(), request.ConversationId, request.ChannelId, MeSummary, request.Body ?? string.Empty,
            DateTimeOffset.Now, [], request.ParentMessageId);
        if (request.ParentMessageId is { } parentId)
        {
            if (!Replies.TryGetValue(parentId, out var list)) Replies[parentId] = list = [];
            list.Add(message);
        }
        else
        {
            var key = request.ConversationId ?? request.ChannelId!.Value;
            if (!Messages.TryGetValue(key, out var list)) Messages[key] = list = [];
            list.Add(message);
        }

        return Task.FromResult(message);
    }

    public Task<MessageDto> EditMessageAsync(Guid messageId, string body, CancellationToken ct = default)
    {
        var message = FindMessage(messageId) ?? throw new ApiException("Message was not found.", 404);
        var updated = message with { Body = body, EditedAt = DateTimeOffset.Now };
        ReplaceMessage(updated);
        return Task.FromResult(updated);
    }

    public Task<MessageDto> DeleteMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        var message = FindMessage(messageId) ?? throw new ApiException("Message was not found.", 404);
        var updated = message with { Body = string.Empty, IsDeleted = true, EditedAt = null };
        ReplaceMessage(updated);
        return Task.FromResult(updated);
    }

    public Task<MessageDto> ToggleReactionAsync(Guid messageId, string emoji, CancellationToken ct = default)
    {
        var message = FindMessage(messageId) ?? throw new ApiException("Message was not found.", 404);
        var reactions = (message.Reactions ?? []).ToList();
        var existing = reactions.FirstOrDefault(r => r.Emoji == emoji);
        if (existing is null) reactions.Add(new ReactionSummaryDto(emoji, 1, true));
        else if (existing.ReactedByMe)
        {
            reactions.Remove(existing);
            if (existing.Count > 1) reactions.Add(existing with { Count = existing.Count - 1, ReactedByMe = false });
        }
        else
        {
            reactions.Remove(existing);
            reactions.Add(existing with { Count = existing.Count + 1, ReactedByMe = true });
        }

        var updated = message with { Reactions = reactions };
        ReplaceMessage(updated);
        return Task.FromResult(updated);
    }

    public Task<IReadOnlyList<ActivityDto>> GetActivityAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ActivityDto>>(Activity.ToList());

    public Task MarkActivityReadAsync(Guid activityId, CancellationToken ct = default)
    {
        var index = Activity.FindIndex(a => a.Id == activityId);
        if (index >= 0) Activity[index] = Activity[index] with { IsUnread = false };
        return Task.CompletedTask;
    }

    public Task MarkAllActivityReadAsync(Guid organizationId, CancellationToken ct = default)
    {
        for (var i = 0; i < Activity.Count; i++) Activity[i] = Activity[i] with { IsUnread = false };
        return Task.CompletedTask;
    }

    private MessageDto? FindMessage(Guid messageId) =>
        Messages.Values.SelectMany(m => m).Concat(Replies.Values.SelectMany(m => m))
            .FirstOrDefault(m => m.Id == messageId);

    private void ReplaceMessage(MessageDto updated)
    {
        foreach (var list in Messages.Values.Concat(Replies.Values))
        {
            var index = list.FindIndex(m => m.Id == updated.Id);
            if (index >= 0)
            {
                list[index] = updated;
                return;
            }
        }
    }

    public Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkspaceDto>>(Workspaces.ToList());

    public Task<WorkspaceDto> CreateWorkspaceAsync(
        Guid organizationId, CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiException("Enter a name for the workspace.", 400);
        var id = Guid.NewGuid();
        var workspace = new WorkspaceDto(
            id, request.Name, request.Description, WorkspaceRole.Owner, 1,
            [new ChannelDto(Guid.NewGuid(), id, "General", null, ChannelKind.Standard)]);
        Workspaces.Add(workspace);
        return Task.FromResult(workspace);
    }

    public Task<IReadOnlyList<WorkspaceMemberDto>> GetWorkspaceMembersAsync(Guid workspaceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkspaceMemberDto>>(
            WorkspaceMembers.TryGetValue(workspaceId, out var members) ? members.ToList() : []);

    public Task AddWorkspaceMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        var member = Members.First(m => m.UserId == userId);
        if (!WorkspaceMembers.TryGetValue(workspaceId, out var members)) WorkspaceMembers[workspaceId] = members = [];
        members.Add(new WorkspaceMemberDto(userId, member.DisplayName, WorkspaceRole.Member, member.Status));
        return Task.CompletedTask;
    }

    public Task<ChannelDto> CreateChannelAsync(Guid workspaceId, CreateChannelRequest request, CancellationToken ct = default)
    {
        var index = Workspaces.FindIndex(w => w.Id == workspaceId);
        var workspace = Workspaces[index];
        if (workspace.Channels.Any(c => string.Equals(c.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException($"This workspace already has a channel named “{request.Name}”.", 409);

        var channel = new ChannelDto(
            Guid.NewGuid(), workspaceId, request.Name, request.Description, request.Kind,
            request.Kind == ChannelKind.Private ? 1 + (request.MemberIds?.Count ?? 0) : workspace.MemberCount);
        Workspaces[index] = workspace with { Channels = [.. workspace.Channels, channel] };
        return Task.FromResult(channel);
    }

    public Task<IReadOnlyList<ChannelMemberDto>> GetChannelMembersAsync(Guid channelId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChannelMemberDto>>([]);

    public Task AddChannelMemberAsync(Guid channelId, Guid userId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveChannelMemberAsync(Guid channelId, Guid userId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ChannelDto> UpdateChannelPrefsAsync(
        Guid channelId, UpdateChannelPrefsRequest request, CancellationToken ct = default)
    {
        foreach (var workspace in Workspaces)
        {
            var channel = workspace.Channels.FirstOrDefault(c => c.Id == channelId);
            if (channel is null) continue;
            return Task.FromResult(channel with { IsMuted = request.IsMuted, NotifyMode = request.NotifyMode });
        }

        throw new ApiException("Channel was not found.", 404);
    }

    public Task<SearchResultDto> SearchAsync(Guid organizationId, string query, CancellationToken ct = default) =>
        Task.FromResult(new SearchResultDto(
            Members.Where(m => m.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(m => new UserSummaryDto(m.UserId, m.DisplayName)).ToList(),
            []));

    public Task<IReadOnlyList<FileDto>> GetFilesAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FileDto>>(Files.ToList());

    public Dictionary<Guid, byte[]> FileContents { get; } = new();

    public Task<FileDto> UploadFileAsync(
        Guid? conversationId,
        Guid? channelId,
        string fileName,
        Stream content,
        CancellationToken ct = default,
        Guid? meetingId = null)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        buffer.Position = 0;
        var contentType = FileTypes.ResolveContentType(fileName, buffer);
        var file = new FileDto(
            Guid.NewGuid(), fileName, contentType, bytes.Length, DateTimeOffset.Now,
            MeSummary, conversationId, channelId, meetingId is null ? "Chat" : "Meeting", meetingId);
        Files.Add(file);
        FileContents[file.Id] = bytes;
        return Task.FromResult(file);
    }

    public async Task DownloadFileAsync(Guid fileId, Stream destination, CancellationToken ct = default)
    {
        if (!FileContents.TryGetValue(fileId, out var bytes)) return;
        await destination.WriteAsync(bytes, ct);
    }

    public Task<IReadOnlyList<MeetingDto>> GetMeetingsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MeetingDto>>(Meetings.ToList());

    public Task<IReadOnlyList<MeetingDto>> GetCalendarAsync(
        Guid organizationId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MeetingDto>>(
            Meetings.Where(m => m.StartsAt >= from && m.StartsAt < to).ToList());

    public Task<MeetingDto> CreateMeetingAsync(Guid organizationId, string title, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var meeting = FakeMeeting(Guid.NewGuid(), title, MeSummary, now, now, false, []);
        Meetings.Add(meeting);
        return Task.FromResult(meeting);
    }

    public Task<GuestMeetingInviteDto> GetGuestMeetingInviteAsync(string token, CancellationToken ct = default) =>
        Task.FromResult(new GuestMeetingInviteDto(
            token, Guid.NewGuid(), "Guest meeting", null, "Host",
            DateTimeOffset.UtcNow.AddHours(1), null, false, true, false,
            "guest@example.com", "Guest",
            "http://localhost/join/?token=" + token,
            "meetjoe://join?token=" + token));

    public Task<GuestJoinDto> JoinGuestMeetingInviteAsync(
        string token, string displayName, CancellationToken ct = default) =>
        Task.FromResult(new GuestJoinDto(
            Guid.NewGuid(), "ws://localhost", "room", "token", false, displayName, Guid.NewGuid()));

    public Task<GuestJoinDto> GetGuestMeetingSessionAsync(string token, CancellationToken ct = default) =>
        JoinGuestMeetingInviteAsync(token, "Guest", ct);

    public Task LeaveGuestMeetingAsync(string token, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<MeetingDto> ScheduleMeetingAsync(
        Guid organizationId, ScheduleMeetingRequest request, CancellationToken ct = default)
    {
        var meeting = FakeMeeting(
            Guid.NewGuid(), request.Title, MeSummary, DateTimeOffset.Now, request.StartsAt, true, [],
            request.Description, request.EndsAt, request.LobbyEnabled,
            (request.InviteeIds ?? []).Select(id => new MeetingInviteDto(
                id, Members.FirstOrDefault(m => m.UserId == id)?.DisplayName ?? "Guest", InviteStatus.Pending)).ToList());
        Meetings.Add(meeting);
        return Task.FromResult(meeting);
    }

    public Task<MeetingJoinDto> JoinMeetingAsync(Guid meetingId, CancellationToken ct = default)
    {
        var index = Meetings.FindIndex(m => m.Id == meetingId);
        if (index < 0) throw new ApiException("This meeting has ended.", 409);

        var meeting = Meetings[index];
        if (meeting.Participants.All(p => p.Id != Me.Id))
            Meetings[index] = meeting with { Participants = [.. meeting.Participants, MeSummary] };
        return Task.FromResult(new MeetingJoinDto(meetingId, "ws://localhost:7880", $"mj-{meetingId:N}", "token"));
    }

    public Task LeaveMeetingAsync(Guid meetingId, CancellationToken ct = default)
    {
        var index = Meetings.FindIndex(m => m.Id == meetingId);
        if (index < 0) return Task.CompletedTask;

        var remaining = Meetings[index].Participants.Where(p => p.Id != Me.Id).ToList();
        if (remaining.Count == 0) Meetings.RemoveAt(index);
        else Meetings[index] = Meetings[index] with { Participants = remaining };
        return Task.CompletedTask;
    }

    public Task EndMeetingAsync(Guid meetingId, CancellationToken ct = default)
    {
        Meetings.RemoveAll(m => m.Id == meetingId);
        return Task.CompletedTask;
    }

    public Task<MeetingDto> RespondToInviteAsync(Guid meetingId, InviteStatus status, CancellationToken ct = default)
    {
        var index = Meetings.FindIndex(m => m.Id == meetingId);
        if (index < 0) throw new ApiException("Meeting was not found.", 404);
        var invites = Meetings[index].Invites.Select(i =>
            i.UserId == Me.Id ? i with { Status = status } : i).ToList();
        Meetings[index] = Meetings[index] with { Invites = invites };
        return Task.FromResult(Meetings[index]);
    }

    public Task<MeetingDto> InviteParticipantsAsync(
        Guid meetingId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        var index = Meetings.FindIndex(m => m.Id == meetingId);
        if (index < 0) throw new ApiException("Meeting was not found.", 404);
        var existing = Meetings[index].Invites.Select(i => i.UserId).ToHashSet();
        var invites = Meetings[index].Invites.Concat(
            userIds.Where(id => id != Me.Id && existing.Add(id)).Select(id =>
                new MeetingInviteDto(
                    id, Members.FirstOrDefault(m => m.UserId == id)?.DisplayName ?? "Guest",
                    InviteStatus.Pending))).ToList();
        Meetings[index] = Meetings[index] with { Invites = invites };
        return Task.FromResult(Meetings[index]);
    }

    public Task<IReadOnlyList<MessageDto>> GetMeetingMessagesAsync(Guid meetingId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MessageDto>>([]);

    public Task<MessageDto> SendMeetingChatAsync(
        Guid meetingId, string? body, CancellationToken ct = default, IReadOnlyList<Guid>? attachmentIds = null) =>
        Task.FromResult(new MessageDto(
            Guid.NewGuid(), null, null, MeSummary, body ?? string.Empty, DateTimeOffset.Now, [], null, null, false, 0, null, meetingId));

    public Task<MeetingRecordingDto> StartRecordingAsync(Guid meetingId, CancellationToken ct = default) =>
        Task.FromResult(new MeetingRecordingDto(
            Guid.NewGuid(), meetingId, RecordingStatus.Recording, null, DateTimeOffset.Now, null));

    public Task<MeetingRecordingDto> CompleteRecordingAsync(
        Guid meetingId, Guid fileId, CancellationToken ct = default) =>
        Task.FromResult(new MeetingRecordingDto(
            Guid.NewGuid(), meetingId, RecordingStatus.Ready, "recording.webm", DateTimeOffset.Now, DateTimeOffset.Now));

    public Task CancelRecordingAsync(Guid meetingId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetAccessTokenAsync() => Task.FromResult<string?>("test-token");

    public Task<IReadOnlyList<CallDto>> GetCallsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CallDto>>(Calls.ToList());

    public Task<CallJoinDto> StartCallAsync(Guid organizationId, Guid conversationId, CancellationToken ct = default)
    {
        var call = new CallDto(
            Guid.NewGuid(), conversationId, CallKind.Direct, MeSummary, DateTimeOffset.Now, null, null, true,
            [new CallParticipantDto(Me.Id, Me.DisplayName, CallParticipantStatus.Joined)]);
        Calls.Insert(0, call);
        return Task.FromResult(new CallJoinDto(call.Id, "ws://localhost:7880", $"call-{call.Id:N}", "token"));
    }

    public Task<CallJoinDto> AnswerCallAsync(Guid callId, CancellationToken ct = default) =>
        Task.FromResult(new CallJoinDto(callId, "ws://localhost:7880", $"call-{callId:N}", "token"));

    public Task DeclineCallAsync(Guid callId, CancellationToken ct = default) => Task.CompletedTask;
    public Task HangUpCallAsync(Guid callId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EndCallAsync(Guid callId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<CallDto> InviteToCallAsync(Guid callId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        var existing = Calls.FirstOrDefault(c => c.Id == callId)
                       ?? throw new ApiException("Call not found.", 404);
        var participants = existing.Participants.ToList();
        foreach (var id in userIds)
        {
            if (participants.Any(p => p.UserId == id)) continue;
            var name = Members.FirstOrDefault(m => m.UserId == id)?.DisplayName ?? "Guest";
            participants.Add(new CallParticipantDto(id, name, CallParticipantStatus.Ringing));
        }

        var updated = existing with
        {
            Kind = participants.Count > 2 ? CallKind.Group : existing.Kind,
            Participants = participants,
        };
        var index = Calls.FindIndex(c => c.Id == callId);
        if (index >= 0) Calls[index] = updated;
        return Task.FromResult(updated);
    }

    internal static MeetingDto FakeMeeting(
        Guid id,
        string title,
        UserSummaryDto host,
        DateTimeOffset createdAt,
        DateTimeOffset startsAt,
        bool isScheduled,
        IReadOnlyList<UserSummaryDto> participants,
        string? description = null,
        DateTimeOffset? endsAt = null,
        bool lobbyEnabled = false,
        IReadOnlyList<MeetingInviteDto>? invites = null) =>
        new(id, title, description, host, createdAt, startsAt, endsAt, true, lobbyEnabled, isScheduled,
            RecurrenceKind.None, null, participants, invites ?? [], [], []);
}

/// <summary>A realtime connection whose events the test raises by hand.</summary>
internal sealed class FakeRealtime(FakeApi api) : IRealtimeClient
{
    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageDto>? MessageUpdated;
    public event Action<TypingDto>? Typing;
    public event Action<ReadReceiptDto>? ReadReceipt;
    public event Action<PresenceDto>? PresenceChanged;
    public event Action? ConversationsChanged;
    public event Action? WorkspacesChanged;
    public event Action? MeetingsChanged;
    public event Action? MembersChanged;
    public event Action? ActivityChanged;
    public event Action<MeetingDto>? MeetingUpdated;
    public event Action<MessageDto>? MeetingChat;
    public event Action<Guid, Guid>? ParticipantMuted;
    public event Action<Guid, Guid>? ParticipantRemoved;
    public event Action<Guid, Guid, bool>? HandRaised;
    public event Action<CallDto>? IncomingCall;
    public event Action<CallDto>? CallUpdated;
    public event Action<Guid, Guid, bool>? CallMicMuted;
    public event Action? CallsChanged;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }
    public PresenceStatus? LastAvailability { get; private set; }

    public Task StartAsync()
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<MessageDto> SendMessageAsync(SendMessageRequest request) => api.SendMessageAsync(request);
    public Task<MessageDto> EditMessageAsync(Guid messageId, string body) => api.EditMessageAsync(messageId, body);
    public Task<MessageDto> DeleteMessageAsync(Guid messageId) => api.DeleteMessageAsync(messageId);
    public Task<MessageDto> ToggleReactionAsync(Guid messageId, string emoji) => api.ToggleReactionAsync(messageId, emoji);
    public Task SetTypingAsync(Guid? conversationId, Guid? channelId, bool isTyping) => Task.CompletedTask;

    public Task<PresenceDto> SetAvailabilityAsync(PresenceStatus availability)
    {
        LastAvailability = availability;
        return Task.FromResult(new PresenceDto(api.Me.Id, availability, api.Me.StatusMessage));
    }

    public Task RaiseHandAsync(Guid meetingId) => Task.CompletedTask;
    public Task LowerHandAsync(Guid meetingId) => Task.CompletedTask;
    public Task AdmitAsync(Guid meetingId, Guid userId) => Task.CompletedTask;
    public Task RejectAsync(Guid meetingId, Guid userId) => Task.CompletedTask;
    public Task RemoveParticipantAsync(Guid meetingId, Guid userId) => Task.CompletedTask;
    public Task MuteParticipantAsync(Guid meetingId, Guid userId) => Task.CompletedTask;
    public Task<MessageDto> SendMeetingChatAsync(Guid meetingId, string? body, IReadOnlyList<Guid>? attachmentIds = null) =>
        api.SendMeetingChatAsync(meetingId, body, attachmentIds: attachmentIds);
    public Task<CallJoinDto> AnswerCallAsync(Guid callId) => api.AnswerCallAsync(callId);
    public Task DeclineCallAsync(Guid callId) => api.DeclineCallAsync(callId);
    public Task HangUpCallAsync(Guid callId) => api.HangUpCallAsync(callId);
    public Task SetCallMicMutedAsync(Guid callId, bool muted) => Task.CompletedTask;

    public void Receive(MessageDto message) => MessageReceived?.Invoke(message);
    public void Update(MessageDto message) => MessageUpdated?.Invoke(message);
    public void RaisePresence(PresenceDto presence) => PresenceChanged?.Invoke(presence);
    public void RaiseConversationsChanged() => ConversationsChanged?.Invoke();
    public void RaiseWorkspacesChanged() => WorkspacesChanged?.Invoke();
    public void RaiseMeetingsChanged() => MeetingsChanged?.Invoke();
    public void RaiseMembersChanged() => MembersChanged?.Invoke();
    public void RaiseActivityChanged() => ActivityChanged?.Invoke();

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }
}

internal sealed class FakeAuth : IAuthService
{
    public bool IsSignedIn { get; private set; }
    public event Action? SessionExpired;

    /// <summary>When set, sign-in fails with this message.</summary>
    public string? Rejection { get; set; }

    public Task SignInAsync(string email, string password, CancellationToken ct = default)
    {
        if (Rejection is not null) throw new ApiException(Rejection, 400);
        IsSignedIn = true;
        return Task.CompletedTask;
    }

    /// <summary>When true, a saved session resumes at startup.</summary>
    public bool HasSavedSession { get; set; }

    public Task<bool> TryResumeAsync(CancellationToken ct = default)
    {
        if (HasSavedSession) IsSignedIn = true;
        return Task.FromResult(IsSignedIn);
    }

    public Task<string?> GetAccessTokenAsync() => Task.FromResult(IsSignedIn ? "token" : null);

    public void SignOut()
    {
        IsSignedIn = false;
        HasSavedSession = false;
    }

    public void Expire() => SessionExpired?.Invoke();
}

internal sealed class FakePlatform : IPlatformServices
{
    public PickedFile? NextPick { get; set; }
    public string? Clipboard { get; private set; }
    public List<Uri> OpenedUris { get; } = [];

    public Task<PickedFile?> PickFileAsync() => Task.FromResult(NextPick);
    /// <summary>What the last download wrote. Readable after the view model has closed it.</summary>
    public MemoryStream? LastSaved { get; private set; }

    public Task<Stream?> OpenSaveStreamAsync(string suggestedName)
    {
        LastSaved = new MemoryStream();
        return Task.FromResult<Stream?>(LastSaved);
    }

    public Task CopyTextAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }

    public Task OpenUriAsync(Uri uri)
    {
        OpenedUris.Add(uri);
        return Task.CompletedTask;
    }

    public bool NextConfirmResult { get; set; } = true;
    public List<(string Title, string Message)> Confirms { get; } = [];

    public Task<bool> ConfirmAsync(
        string title, string message, string confirmLabel = "Confirm", string cancelLabel = "Cancel")
    {
        Confirms.Add((title, message));
        return Task.FromResult(NextConfirmResult);
    }

    public List<(string Title, string Message)> Alerts { get; } = [];

    public Task AlertAsync(string title, string message, string okLabel = "OK")
    {
        Alerts.Add((title, message));
        return Task.CompletedTask;
    }

    public int ImageLightboxCount { get; private set; }
    public int FilePreviewCount { get; private set; }
    public string? LastPreviewFileName { get; private set; }

    public Task ShowImageAsync(Avalonia.Media.Imaging.Bitmap image)
    {
        ImageLightboxCount++;
        return Task.CompletedTask;
    }

    public Task ShowFilePreviewAsync(string fileName, Stream content, string? contentType = null)
    {
        FilePreviewCount++;
        LastPreviewFileName = fileName;
        return Task.CompletedTask;
    }
}

/// <summary>A small, believable organization to drive view models and screenshots.</summary>
internal sealed class World
{
    public FakeApi Api { get; } = new();
    public FakeRealtime Realtime { get; }
    public FakeAuth Auth { get; } = new();
    public FakePlatform Platform { get; } = new();
    public AppConfig Config { get; } = new();

    public UserSummaryDto Ana => Api.MeSummary;
    public UserSummaryDto Ben { get; } = new(Guid.NewGuid(), "Ben Okafor");
    public UserSummaryDto Chloe { get; } = new(Guid.NewGuid(), "Chloe Tan");
    public UserSummaryDto David { get; } = new(Guid.NewGuid(), "David Kim");
    public UserSummaryDto Priya { get; } = new(Guid.NewGuid(), "Priya Nair");

    public OrganizationDto Organization { get; } = new(Guid.NewGuid(), "Northwind Studio", OrgRole.Owner, "K7MPQ-2XHWD");

    public ConversationDto WithBen { get; }
    public ConversationDto LaunchGroup { get; }
    public ConversationDto WithPriya { get; }
    public WorkspaceDto Product { get; }

    public World()
    {
        Realtime = new FakeRealtime(Api);
        Api.Organizations.Add(Organization);

        Api.Members.AddRange(
        [
            new OrgMemberDto(Ana.Id, Ana.DisplayName, "ana@northwind.example", OrgRole.Owner, PresenceStatus.Available, null),
            new OrgMemberDto(Ben.Id, Ben.DisplayName, "ben@northwind.example", OrgRole.Member, PresenceStatus.Available, "Heads down on the release"),
            new OrgMemberDto(Chloe.Id, Chloe.DisplayName, "chloe@northwind.example", OrgRole.Member, PresenceStatus.Busy, null),
            new OrgMemberDto(David.Id, David.DisplayName, "david@northwind.example", OrgRole.Member, PresenceStatus.Offline, null),
            new OrgMemberDto(Priya.Id, Priya.DisplayName, "priya@northwind.example", OrgRole.Admin, PresenceStatus.Away, null),
        ]);

        var now = DateTimeOffset.Now;
        WithBen = new ConversationDto(
            Guid.NewGuid(), ConversationKind.Direct, null, [Ana, Ben],
            "Sounds good — I'll send the build over after lunch.", now.AddMinutes(-4), 0);
        LaunchGroup = new ConversationDto(
            Guid.NewGuid(), ConversationKind.Group, "Launch planning", [Ana, Ben, Chloe],
            "Chloe: Draft of the announcement is in the channel.", now.AddMinutes(-38), 2,
            CreatedById: Ana.Id);
        WithPriya = new ConversationDto(
            Guid.NewGuid(), ConversationKind.Direct, null, [Ana, Priya],
            "Thanks for the invoice details.", now.AddDays(-1), 0);
        Api.Conversations.AddRange([WithBen, LaunchGroup, WithPriya]);

        Api.NewMessage(WithBen.Id, null, Ben, "Morning! Did the installer fix make it into last night's build?", now.AddMinutes(-21));
        Api.NewMessage(WithBen.Id, null, Ana, "It did. Signed and notarized, both platforms.", now.AddMinutes(-19));
        Api.NewMessage(WithBen.Id, null, Ana, "Windows still needs a pass on the high-DPI icons, but nothing blocking.", now.AddMinutes(-19));
        Api.NewMessage(WithBen.Id, null, Ben, "Great. Can you share it with the pilot group today?", now.AddMinutes(-6));
        Api.NewMessage(WithBen.Id, null, Ana, "Sounds good — I'll send the build over after lunch.", now.AddMinutes(-4));

        var productId = Guid.NewGuid();
        var general = new ChannelDto(Guid.NewGuid(), productId, "General", null, ChannelKind.Standard);
        Product = new WorkspaceDto(
            productId, "Product", "Planning and delivery", WorkspaceRole.Owner, 4,
            [
                general,
                new ChannelDto(Guid.NewGuid(), productId, "Roadmap", null, ChannelKind.Standard),
                new ChannelDto(Guid.NewGuid(), productId, "Design reviews", null, ChannelKind.Standard),
            ]);
        var engineeringId = Guid.NewGuid();
        Api.Workspaces.AddRange(
        [
            Product,
            new WorkspaceDto(
                engineeringId, "Engineering", null, WorkspaceRole.Member, 3,
                [
                    new ChannelDto(Guid.NewGuid(), engineeringId, "General", null, ChannelKind.Standard),
                    new ChannelDto(Guid.NewGuid(), engineeringId, "Releases", null, ChannelKind.Standard),
                ]),
        ]);
        Api.WorkspaceMembers[productId] =
        [
            new WorkspaceMemberDto(Ana.Id, Ana.DisplayName, WorkspaceRole.Owner, PresenceStatus.Available),
            new WorkspaceMemberDto(Ben.Id, Ben.DisplayName, WorkspaceRole.Member, PresenceStatus.Available),
            new WorkspaceMemberDto(Chloe.Id, Chloe.DisplayName, WorkspaceRole.Member, PresenceStatus.Busy),
            new WorkspaceMemberDto(Priya.Id, Priya.DisplayName, WorkspaceRole.Member, PresenceStatus.Away),
        ];

        Api.NewMessage(null, general.Id, Chloe, "Draft of the launch announcement is attached. Comments welcome before Thursday.", now.AddHours(-3));
        Api.Messages[general.Id][0] = Api.Messages[general.Id][0] with
        {
            Attachments = [new AttachmentDto(Guid.NewGuid(), "Launch announcement v2.docx", "application/octet-stream", 184_320)],
        };
        Api.NewMessage(null, general.Id, Ben, "Reads well. One suggestion: lead with the offline support, it's what the pilot group asked about most.", now.AddHours(-2));
        Api.NewMessage(null, general.Id, Ana, "Agreed. I'll add the numbers from the pilot survey too.", now.AddMinutes(-52));

        Api.Files.AddRange(
        [
            new FileDto(Guid.NewGuid(), "Launch announcement v2.docx", "application/octet-stream", 184_320, now.AddHours(-3), Chloe, null, general.Id, "Product / General"),
            new FileDto(Guid.NewGuid(), "Pilot survey results.xlsx", "application/octet-stream", 2_411_724, now.AddDays(-1), Priya, WithPriya.Id, null, "Chat with Priya Nair"),
            new FileDto(Guid.NewGuid(), "installer-icons.zip", "application/octet-stream", 48_203_110, now.AddDays(-3), Ben, WithBen.Id, null, "Chat with Ben Okafor"),
        ]);

        Api.Meetings.Add(FakeApi.FakeMeeting(
            Guid.NewGuid(), "Weekly product sync", Ben, now.AddMinutes(-12), now.AddMinutes(-12), false, [Ben, Chloe]));
    }

    public SessionContext NewSession()
    {
        var people = new PeopleStore(Api.Me.Id);
        people.Get(Api.Me.Id, Api.Me.DisplayName);
        people.Apply(Api.Members);
        Realtime.StartAsync();
        return new SessionContext(Api, Realtime, Platform, Config, Organization, people, new NullCallTonePlayer());
    }

    public MainViewModel NewApp() => new(Config, Auth, Api, Realtime, Platform, new NullCallTonePlayer());
}
