using MeetJoe.Contracts;

namespace MeetJoe.App.Services;

public sealed class AppConfig
{
    public const string ApiUrlVariable = "MEETJOE_API_URL";
#if DEBUG
    public const string DefaultApiUrl = "http://localhost:5080";
#else
    public const string DefaultApiUrl = "https://api.meetjoe.joesolutions.ph";
#endif

    public Uri ApiBaseUri { get; init; } = new(DefaultApiUrl);

    public static AppConfig FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(ApiUrlVariable);
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return new AppConfig();

        // Relative request paths resolve against the base, which only works when it ends in a slash.
        return new AppConfig
        {
            ApiBaseUri = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + '/'),
        };
    }
}

/// <summary>A failure the user can be told about as-is.</summary>
public sealed class ApiException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// Keeps the refresh token between launches in the OS credential store (macOS Keychain,
/// Windows DPAPI). Implementations must not throw; a failed save just means signing in again.
/// </summary>
public interface ISessionStore
{
    string? LoadRefreshToken();
    void SaveRefreshToken(string refreshToken);
    void Clear();
}

/// <summary>Memory only: the user signs in on every launch (tests, unsupported platforms).</summary>
public sealed class NullSessionStore : ISessionStore
{
    public string? LoadRefreshToken() => null;
    public void SaveRefreshToken(string refreshToken) { }
    public void Clear() { }
}

public interface IAuthService
{
    bool IsSignedIn { get; }

    /// <summary>Raised when the session can no longer be refreshed and the user must sign in again.</summary>
    event Action? SessionExpired;

    Task SignInAsync(string email, string password, CancellationToken ct = default);

    /// <summary>
    /// Signs in silently with the refresh token saved by a previous launch. False when there is
    /// none, the server rejected it, or the server is unreachable.
    /// </summary>
    Task<bool> TryResumeAsync(CancellationToken ct = default);

    /// <summary>A valid access token, refreshed if needed, or null when signed out.</summary>
    Task<string?> GetAccessTokenAsync();

    void SignOut();
}

public interface IMeetJoeApi
{
    Task RegisterAsync(RegisterRequest request, CancellationToken ct = default);

    Task<MeDto> GetMeAsync(CancellationToken ct = default);
    Task<MeDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default);
    Task<MeDto> UploadAvatarAsync(string fileName, Stream content, CancellationToken ct = default);
    Task<MeDto> ClearAvatarAsync(CancellationToken ct = default);
    Task<Stream> DownloadAvatarAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken ct = default);
    Task<OrganizationDto> CreateOrganizationAsync(string name, CancellationToken ct = default);
    Task<OrganizationDto> JoinOrganizationAsync(string joinCode, CancellationToken ct = default);
    Task LeaveOrganizationAsync(Guid organizationId, CancellationToken ct = default);
    Task<IReadOnlyList<OrgMemberDto>> GetMembersAsync(Guid organizationId, CancellationToken ct = default);

    Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(Guid organizationId, CancellationToken ct = default);
    Task<ConversationDto> CreateConversationAsync(Guid organizationId, CreateConversationRequest request, CancellationToken ct = default);
    Task<ConversationDto> UpdateConversationPrefsAsync(Guid conversationId, UpdateConversationPrefsRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid? conversationId, Guid? channelId, DateTimeOffset? before, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetRepliesAsync(Guid parentMessageId, CancellationToken ct = default);
    Task MarkConversationReadAsync(Guid conversationId, CancellationToken ct = default);
    Task MarkChannelReadAsync(Guid channelId, CancellationToken ct = default);
    Task<MessageDto> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default);
    Task<MessageDto> EditMessageAsync(Guid messageId, string body, CancellationToken ct = default);
    Task<MessageDto> DeleteMessageAsync(Guid messageId, CancellationToken ct = default);
    Task<MessageDto> ToggleReactionAsync(Guid messageId, string emoji, CancellationToken ct = default);

    Task<IReadOnlyList<ActivityDto>> GetActivityAsync(Guid organizationId, CancellationToken ct = default);
    Task MarkActivityReadAsync(Guid activityId, CancellationToken ct = default);
    Task MarkAllActivityReadAsync(Guid organizationId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(Guid organizationId, CancellationToken ct = default);
    Task<WorkspaceDto> CreateWorkspaceAsync(Guid organizationId, CreateWorkspaceRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMemberDto>> GetWorkspaceMembersAsync(Guid workspaceId, CancellationToken ct = default);
    Task AddWorkspaceMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<ChannelDto> CreateChannelAsync(Guid workspaceId, CreateChannelRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ChannelMemberDto>> GetChannelMembersAsync(Guid channelId, CancellationToken ct = default);
    Task AddChannelMemberAsync(Guid channelId, Guid userId, CancellationToken ct = default);
    Task RemoveChannelMemberAsync(Guid channelId, Guid userId, CancellationToken ct = default);
    Task<ChannelDto> UpdateChannelPrefsAsync(Guid channelId, UpdateChannelPrefsRequest request, CancellationToken ct = default);

    Task<SearchResultDto> SearchAsync(Guid organizationId, string query, CancellationToken ct = default);

    Task<IReadOnlyList<FileDto>> GetFilesAsync(Guid organizationId, CancellationToken ct = default);
    Task<FileDto> UploadFileAsync(
        Guid? conversationId,
        Guid? channelId,
        string fileName,
        Stream content,
        CancellationToken ct = default,
        Guid? meetingId = null);
    Task DownloadFileAsync(Guid fileId, Stream destination, CancellationToken ct = default);

    Task<IReadOnlyList<MeetingDto>> GetMeetingsAsync(Guid organizationId, CancellationToken ct = default);
    Task<IReadOnlyList<MeetingDto>> GetCalendarAsync(Guid organizationId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<MeetingDto> CreateMeetingAsync(Guid organizationId, string title, CancellationToken ct = default);
    Task<MeetingDto> ScheduleMeetingAsync(Guid organizationId, ScheduleMeetingRequest request, CancellationToken ct = default);
    Task<MeetingJoinDto> JoinMeetingAsync(Guid meetingId, CancellationToken ct = default);
    Task LeaveMeetingAsync(Guid meetingId, CancellationToken ct = default);
    Task EndMeetingAsync(Guid meetingId, CancellationToken ct = default);
    Task<MeetingDto> RespondToInviteAsync(Guid meetingId, InviteStatus status, CancellationToken ct = default);
    Task<MeetingDto> InviteParticipantsAsync(Guid meetingId, IReadOnlyList<Guid> userIds, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetMeetingMessagesAsync(Guid meetingId, CancellationToken ct = default);
    Task<MessageDto> SendMeetingChatAsync(
        Guid meetingId, string? body, CancellationToken ct = default, IReadOnlyList<Guid>? attachmentIds = null);
    Task<MeetingRecordingDto> StartRecordingAsync(Guid meetingId, CancellationToken ct = default);
    Task<MeetingRecordingDto> CompleteRecordingAsync(Guid meetingId, Guid fileId, CancellationToken ct = default);
    Task CancelRecordingAsync(Guid meetingId, CancellationToken ct = default);
    Task<string?> GetAccessTokenAsync();

    Task<IReadOnlyList<CallDto>> GetCallsAsync(Guid organizationId, CancellationToken ct = default);
    Task<CallJoinDto> StartCallAsync(Guid organizationId, Guid conversationId, CancellationToken ct = default);
    Task<CallJoinDto> AnswerCallAsync(Guid callId, CancellationToken ct = default);
    Task DeclineCallAsync(Guid callId, CancellationToken ct = default);
    Task HangUpCallAsync(Guid callId, CancellationToken ct = default);
    Task EndCallAsync(Guid callId, CancellationToken ct = default);
    Task<CallDto> InviteToCallAsync(Guid callId, IReadOnlyList<Guid> userIds, CancellationToken ct = default);
}

/// <summary>Live connection to the server. Every event is raised on the UI thread.</summary>
public interface IRealtimeClient
{
    event Action<MessageDto>? MessageReceived;
    event Action<MessageDto>? MessageUpdated;
    event Action<TypingDto>? Typing;
    event Action<ReadReceiptDto>? ReadReceipt;
    event Action<PresenceDto>? PresenceChanged;
    event Action? ConversationsChanged;
    event Action? WorkspacesChanged;
    event Action? MeetingsChanged;
    event Action? MembersChanged;
    event Action? ActivityChanged;
    event Action<MeetingDto>? MeetingUpdated;
    event Action<MessageDto>? MeetingChat;
    event Action<Guid, Guid>? ParticipantMuted;
    event Action<Guid, Guid>? ParticipantRemoved;
    event Action<Guid, Guid, bool>? HandRaised;
    event Action<CallDto>? IncomingCall;
    event Action<CallDto>? CallUpdated;
    /// <summary>Args: callId, userId, muted.</summary>
    event Action<Guid, Guid, bool>? CallMicMuted;
    event Action? CallsChanged;

    /// <summary>True when connected, false while reconnecting. A change to true means events may have been missed.</summary>
    event Action<bool>? ConnectionChanged;

    bool IsConnected { get; }

    Task StartAsync();
    Task StopAsync();
    Task<MessageDto> SendMessageAsync(SendMessageRequest request);
    Task<MessageDto> EditMessageAsync(Guid messageId, string body);
    Task<MessageDto> DeleteMessageAsync(Guid messageId);
    Task<MessageDto> ToggleReactionAsync(Guid messageId, string emoji);
    Task SetTypingAsync(Guid? conversationId, Guid? channelId, bool isTyping);
    Task<PresenceDto> SetAvailabilityAsync(PresenceStatus availability);
    Task RaiseHandAsync(Guid meetingId);
    Task LowerHandAsync(Guid meetingId);
    Task AdmitAsync(Guid meetingId, Guid userId);
    Task RejectAsync(Guid meetingId, Guid userId);
    Task RemoveParticipantAsync(Guid meetingId, Guid userId);
    Task MuteParticipantAsync(Guid meetingId, Guid userId);
    Task<MessageDto> SendMeetingChatAsync(Guid meetingId, string? body, IReadOnlyList<Guid>? attachmentIds = null);
    Task<CallJoinDto> AnswerCallAsync(Guid callId);
    Task DeclineCallAsync(Guid callId);
    Task HangUpCallAsync(Guid callId);
    Task SetCallMicMutedAsync(Guid callId, bool muted);
}

public sealed record PickedFile(string Name, Func<Task<Stream>> OpenReadAsync);

/// <summary>File dialogs, clipboard, and opening URLs — all need the platform window or OS.</summary>
public interface IPlatformServices
{
    Task<PickedFile?> PickFileAsync();
    Task<Stream?> OpenSaveStreamAsync(string suggestedName);
    Task CopyTextAsync(string text);

    /// <summary>Opens a URI in the default OS handler (fallback only; media stays in-app).</summary>
    Task OpenUriAsync(Uri uri);

    /// <summary>Shows a modal yes/no confirmation. Returns true when the user confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm", string cancelLabel = "Cancel");

    /// <summary>Shows a modal notice with a single dismiss action.</summary>
    Task AlertAsync(string title, string message, string okLabel = "OK");

    /// <summary>Shows an image full-window with a close control; dismisses when the user closes it.</summary>
    Task ShowImageAsync(Avalonia.Media.Imaging.Bitmap image);

    /// <summary>Maximized file preview popup (image or text) with a close control in the upper right.</summary>
    Task ShowFilePreviewAsync(string fileName, Stream content, string? contentType = null);
}
