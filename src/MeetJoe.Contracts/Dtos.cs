namespace MeetJoe.Contracts;

/// <summary>Public desktop update feed (semver). Clients use FeedUrl with Velopack for delta installs.</summary>
public record DesktopUpdateInfoDto(
    string Version,
    string Channel,
    string Rid,
    string FeedUrl,
    string? ReleaseNotes,
    bool Mandatory,
    DateTimeOffset? PublishedAt);

// Wire contracts shared by the API and every client. Enums travel as their string names.

public enum PresenceStatus { Offline, Available, Away, Busy, DoNotDisturb }

public enum OrgRole { Owner, Admin, Member, Guest }

public enum ConversationKind { Direct, Group }

public enum ChannelKind { Standard, Private, Shared }

public enum WorkspaceRole { Owner, Member }

public enum ChannelRole { Owner, Member }

public enum NotifyMode { All, Mentions, None }

public enum ActivityKind { Reply, Reaction, Mention, MeetingInvite, MissedCall }

public enum CallKind { Direct, Group }

public enum CallParticipantStatus { Ringing, Joined, Declined, Missed, Left }

public enum InviteStatus { Pending, Accepted, Declined }

public enum RecurrenceKind { None, Daily, Weekly }

public enum RecordingStatus { Unavailable, Starting, Recording, Ready, Failed }

// ---- Identity & profile ---------------------------------------------------

public record RegisterRequest(string Email, string Password, string DisplayName);

public record UserSummaryDto(Guid Id, string DisplayName, string? AvatarUrl = null);

public record MeDto(
    Guid Id,
    string Email,
    string DisplayName,
    PresenceStatus Availability,
    string? StatusMessage,
    string? AvatarUrl = null);

public record UpdateProfileRequest(string DisplayName, string? StatusMessage);

public record SetPresenceRequest(PresenceStatus Availability);

public record PresenceDto(Guid UserId, PresenceStatus Status, string? StatusMessage);

// ---- Organizations --------------------------------------------------------

/// <param name="JoinCode">Only populated for owners and admins.</param>
public record OrganizationDto(Guid Id, string Name, OrgRole Role, string? JoinCode);

public record CreateOrganizationRequest(string Name);

public record JoinOrganizationRequest(string JoinCode);

public record OrgMemberDto(
    Guid UserId,
    string DisplayName,
    string Email,
    OrgRole Role,
    PresenceStatus Status,
    string? StatusMessage,
    string? AvatarUrl = null);

// ---- Chat -----------------------------------------------------------------

/// <summary>How far a member has read in a conversation, channel, or meeting chat.</summary>
public record ReadCursorDto(Guid UserId, string DisplayName, DateTimeOffset ReadAt);

public record ConversationDto(
    Guid Id,
    ConversationKind Kind,
    string? Title,
    IReadOnlyList<UserSummaryDto> Members,
    string? LastMessagePreview,
    DateTimeOffset? LastMessageAt,
    int UnreadCount,
    bool IsMuted = false,
    bool IsPinned = false,
    NotifyMode NotifyMode = NotifyMode.All,
    bool HasUnreadMention = false,
    IReadOnlyList<ReadCursorDto>? ReadCursors = null,
    Guid? CreatedById = null);

public record CreateConversationRequest(IReadOnlyList<Guid> MemberIds, string? Title);

public record AddConversationMemberRequest(Guid UserId);

public record RenameConversationRequest(string Title);

public record UpdateConversationPrefsRequest(bool IsMuted, bool IsPinned, NotifyMode NotifyMode);

public record AttachmentDto(Guid FileId, string FileName, string ContentType, long Size);

public record ReactionSummaryDto(string Emoji, int Count, bool ReactedByMe);

public record LinkPreviewDto(string Url, string? Title, string? Description);

public record MessageDto(
    Guid Id,
    Guid? ConversationId,
    Guid? ChannelId,
    UserSummaryDto Sender,
    string Body,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AttachmentDto> Attachments,
    Guid? ParentMessageId = null,
    DateTimeOffset? EditedAt = null,
    bool IsDeleted = false,
    int ReplyCount = 0,
    IReadOnlyList<ReactionSummaryDto>? Reactions = null,
    Guid? MeetingId = null,
    LinkPreviewDto? LinkPreview = null);

/// <summary>Exactly one of conversation, channel, or meeting is set.</summary>
public record SendMessageRequest(
    Guid? ConversationId,
    Guid? ChannelId,
    string? Body,
    IReadOnlyList<Guid>? AttachmentIds,
    Guid? ParentMessageId = null,
    Guid? MeetingId = null);

public record EditMessageRequest(string Body);

public record ToggleReactionRequest(string Emoji);

public record TypingDto(Guid? ConversationId, Guid? ChannelId, Guid UserId, string DisplayName, bool IsTyping);

public record ReadReceiptDto(
    Guid? ConversationId,
    Guid? ChannelId,
    Guid UserId,
    DateTimeOffset ReadAt,
    Guid? MeetingId = null);

public record ActivityDto(
    Guid Id,
    ActivityKind Kind,
    UserSummaryDto Actor,
    string Summary,
    Guid? MessageId,
    Guid? ConversationId,
    Guid? ChannelId,
    DateTimeOffset CreatedAt,
    bool IsUnread,
    Guid? MeetingId = null,
    Guid? CallId = null);

// ---- Calls ----------------------------------------------------------------

public record CallParticipantDto(Guid UserId, string DisplayName, CallParticipantStatus Status);

public record CallDto(
    Guid Id,
    Guid ConversationId,
    CallKind Kind,
    UserSummaryDto Initiator,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? EndedAt,
    bool IsActive,
    IReadOnlyList<CallParticipantDto> Participants);

public record StartCallRequest(Guid ConversationId);

public record InviteToCallRequest(IReadOnlyList<Guid>? UserIds);

public record CallJoinDto(Guid CallId, string ServerUrl, string RoomName, string Token);

// ---- Search ---------------------------------------------------------------

public record SearchResultDto(
    IReadOnlyList<UserSummaryDto> People,
    IReadOnlyList<MessageSearchHitDto> Messages);

public record MessageSearchHitDto(
    Guid MessageId,
    Guid? ConversationId,
    Guid? ChannelId,
    string BodyPreview,
    UserSummaryDto Sender,
    DateTimeOffset CreatedAt,
    string Location);

// ---- Workspaces -----------------------------------------------------------

public record ChannelDto(
    Guid Id, Guid WorkspaceId, string Name, string? Description, ChannelKind Kind,
    int MemberCount = 0, bool IsMuted = false, NotifyMode NotifyMode = NotifyMode.All);

public record WorkspaceDto(
    Guid Id, string Name, string? Description, WorkspaceRole Role, int MemberCount, IReadOnlyList<ChannelDto> Channels);

public record CreateWorkspaceRequest(string Name, string? Description);

public record CreateChannelRequest(
    string Name, string? Description, ChannelKind Kind = ChannelKind.Standard, IReadOnlyList<Guid>? MemberIds = null);

public record AddWorkspaceMemberRequest(Guid UserId);

public record AddChannelMemberRequest(Guid UserId);

public record UpdateChannelPrefsRequest(bool IsMuted, NotifyMode NotifyMode);

public record WorkspaceMemberDto(Guid UserId, string DisplayName, WorkspaceRole Role, PresenceStatus Status);

public record ChannelMemberDto(
    Guid UserId,
    string DisplayName,
    ChannelRole Role,
    PresenceStatus Status,
    DateTimeOffset? LastReadAt = null);

// ---- Files ----------------------------------------------------------------

/// <param name="Location">Human-readable place the file was shared, e.g. "Design / General".</param>
public record FileDto(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    DateTimeOffset CreatedAt,
    UserSummaryDto Uploader,
    Guid? ConversationId,
    Guid? ChannelId,
    string Location,
    Guid? MeetingId = null);

// ---- Meetings -------------------------------------------------------------

public record MeetingDto(
    Guid Id,
    string Title,
    string? Description,
    UserSummaryDto Host,
    DateTimeOffset CreatedAt,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool IsActive,
    bool LobbyEnabled,
    bool IsScheduled,
    RecurrenceKind Recurrence,
    DateTimeOffset? RecurrenceUntil,
    IReadOnlyList<UserSummaryDto> Participants,
    IReadOnlyList<MeetingInviteDto> Invites,
    IReadOnlyList<UserSummaryDto> Lobby,
    IReadOnlyList<Guid> RaisedHands,
    IReadOnlyList<ReadCursorDto>? ChatReadCursors = null);

public record MeetingInviteDto(Guid UserId, string DisplayName, InviteStatus Status);

public record CreateMeetingRequest(string Title);

public record ScheduleMeetingRequest(
    string Title,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool LobbyEnabled,
    IReadOnlyList<Guid>? InviteeIds,
    RecurrenceKind Recurrence = RecurrenceKind.None,
    DateTimeOffset? RecurrenceUntil = null,
    /// <summary>External or org emails that receive a join-link invite.</summary>
    IReadOnlyList<string>? InviteeEmails = null);

/// <summary>Public meeting details for an email / deep-link invite (no auth).</summary>
public record GuestMeetingInviteDto(
    string Token,
    Guid MeetingId,
    string Title,
    string? Description,
    string HostName,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool LobbyEnabled,
    bool IsActive,
    bool HasEnded,
    string? InviteeEmail,
    string? SuggestedDisplayName,
    string WebJoinUrl,
    string DesktopDeepLink);

public record GuestJoinRequest(string DisplayName);

public record GuestChatRequest(string? Body);

public record GuestJoinDto(
    Guid MeetingId,
    string ServerUrl,
    string RoomName,
    string Token,
    bool InLobby,
    string DisplayName,
    Guid GuestUserId,
    /// <summary>False after leave/reject — guest may rejoin the lobby from the invite link.</summary>
    bool IsPresent = true);

public record RespondInviteRequest(InviteStatus Status);

public record InviteParticipantsRequest(IReadOnlyList<Guid>? UserIds);

/// <summary>Everything a client needs to connect to the media room for a meeting.</summary>
public record MeetingJoinDto(
    Guid MeetingId, string ServerUrl, string RoomName, string Token, bool InLobby = false);

public record MeetingRecordingDto(
    Guid Id, Guid MeetingId, RecordingStatus Status, string? FileName, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

public record CompleteRecordingRequest(Guid FileId);

public record MeetingChatMessageDto(MessageDto Message);

// ---- Errors ---------------------------------------------------------------

/// <summary>Subset of RFC 9457 problem details that the API returns for every failure.</summary>
public record ApiProblem(string? Title, string? Detail, int? Status, string? Code);
