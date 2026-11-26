using MeetJoe.Contracts;
using MeetJoe.Domain;
using Microsoft.EntityFrameworkCore;

namespace MeetJoe.Application.Common;

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<Membership> Memberships { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ConversationMember> ConversationMembers { get; }
    DbSet<Message> Messages { get; }
    DbSet<MessageReaction> MessageReactions { get; }
    DbSet<Attachment> Attachments { get; }
    DbSet<FileObject> Files { get; }
    DbSet<ActivityItem> ActivityItems { get; }
    DbSet<Workspace> Workspaces { get; }
    DbSet<WorkspaceMember> WorkspaceMembers { get; }
    DbSet<Channel> Channels { get; }
    DbSet<ChannelMember> ChannelMembers { get; }
    DbSet<Meeting> Meetings { get; }
    DbSet<MeetingParticipant> MeetingParticipants { get; }
    DbSet<MeetingInvite> MeetingInvites { get; }
    DbSet<MeetingGuestInvite> MeetingGuestInvites { get; }
    DbSet<MeetingRecording> MeetingRecordings { get; }
    DbSet<Call> Calls { get; }
    DbSet<CallParticipant> CallParticipants { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Pushes events to connected clients. Implemented over SignalR in the API host.</summary>
public interface IRealtimePublisher
{
    Task MessageCreatedAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default);
    Task MessageUpdatedAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default);
    Task TypingAsync(IReadOnlyCollection<Guid> userIds, TypingDto typing, CancellationToken ct = default);
    Task ReadReceiptAsync(IReadOnlyCollection<Guid> userIds, ReadReceiptDto receipt, CancellationToken ct = default);
    Task PresenceChangedAsync(IReadOnlyCollection<Guid> userIds, PresenceDto presence, CancellationToken ct = default);
    Task ConversationsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task WorkspacesChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task MeetingsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task MembersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task ActivityChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task MeetingUpdatedAsync(IReadOnlyCollection<Guid> userIds, MeetingDto meeting, CancellationToken ct = default);
    Task MeetingChatAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default);
    Task ParticipantMutedAsync(IReadOnlyCollection<Guid> userIds, Guid meetingId, Guid targetUserId, CancellationToken ct = default);
    Task ParticipantRemovedAsync(IReadOnlyCollection<Guid> userIds, Guid meetingId, Guid targetUserId, CancellationToken ct = default);
    Task HandRaisedAsync(IReadOnlyCollection<Guid> userIds, Guid meetingId, Guid userId, bool raised, CancellationToken ct = default);
    Task IncomingCallAsync(IReadOnlyCollection<Guid> userIds, CallDto call, CancellationToken ct = default);
    Task CallUpdatedAsync(IReadOnlyCollection<Guid> userIds, CallDto call, CancellationToken ct = default);
    Task CallsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task CallMicMutedAsync(IReadOnlyCollection<Guid> userIds, Guid callId, Guid userId, bool muted, CancellationToken ct = default);
}

/// <summary>Tracks which users have at least one live realtime connection.</summary>
public interface IPresenceStore
{
    /// <returns>True when this is the user's first connection (they just came online).</returns>
    Task<bool> AddConnectionAsync(Guid userId, string connectionId);

    /// <returns>True when this was the user's last connection (they just went offline).</returns>
    Task<bool> RemoveConnectionAsync(Guid userId, string connectionId);

    Task<IReadOnlySet<Guid>> GetOnlineAsync(IReadOnlyCollection<Guid> userIds);
}

public interface IFileStorage
{
    Task PutAsync(string key, Stream content, long size, string contentType, CancellationToken ct = default);
    Task CopyToAsync(string key, Stream destination, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed record MeetingConnection(string ServerUrl, string Token);

/// <summary>Issues short-lived credentials for the media SFU.</summary>
public interface IMeetingTokenIssuer
{
    MeetingConnection Issue(string roomName, Guid userId, string displayName, bool canPublish = true, bool roomAdmin = false);
}

/// <summary>Outbound email (SMTP or logged sink in development).</summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>Creates or reuses guest accounts for email invitees who are not org members.</summary>
public interface IGuestAccountService
{
    Task<User> GetOrCreateGuestAsync(string email, string displayName, CancellationToken ct = default);
}

/// <summary>Public site / deep-link base URLs used in invite emails.</summary>
public sealed class PublicUrlsOptions
{
    public const string Section = "PublicUrls";

    /// <summary>HTTPS origin of the API / web host, e.g. https://api.meetjoe.example.</summary>
    public string WebBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Custom URL scheme registered by the desktop app.</summary>
    public string DesktopScheme { get; set; } = "meetjoe";

    public string JoinUrl(string token) =>
        $"{WebBaseUrl.TrimEnd('/')}/join/?token={Uri.EscapeDataString(token)}";

    public string DesktopDeepLink(string token) =>
        $"{DesktopScheme}://join?token={Uri.EscapeDataString(token)}";
}
