using MeetJoe.Domain;
using Microsoft.EntityFrameworkCore;
using Contract = MeetJoe.Contracts;

namespace MeetJoe.Application.Common;

internal static class Mapping
{
    /// <summary>Relative API path clients resolve against the MeetJoe API base.</summary>
    public static string? AvatarUrl(Guid userId, string? storageKey) =>
        storageKey is null ? null : $"api/users/{userId:D}/avatar";

    public static Contract.UserSummaryDto ToSummary(this User user) =>
        new(user.Id, user.DisplayName, AvatarUrl(user.Id, user.AvatarStorageKey));

    public static Contract.OrgRole ToContract(this OrgRole role) => role switch
    {
        OrgRole.Owner => Contract.OrgRole.Owner,
        OrgRole.Admin => Contract.OrgRole.Admin,
        OrgRole.Member => Contract.OrgRole.Member,
        OrgRole.Guest => Contract.OrgRole.Guest,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static Contract.WorkspaceRole ToContract(this WorkspaceRole role) => role switch
    {
        WorkspaceRole.Owner => Contract.WorkspaceRole.Owner,
        WorkspaceRole.Member => Contract.WorkspaceRole.Member,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static Contract.ConversationKind ToContract(this ConversationKind kind) => kind switch
    {
        ConversationKind.Direct => Contract.ConversationKind.Direct,
        ConversationKind.Group => Contract.ConversationKind.Group,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static Contract.ChannelKind ToContract(this ChannelKind kind) => kind switch
    {
        ChannelKind.Standard => Contract.ChannelKind.Standard,
        ChannelKind.Private => Contract.ChannelKind.Private,
        ChannelKind.Shared => Contract.ChannelKind.Shared,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static Contract.ActivityKind ToContract(this ActivityKind kind) => kind switch
    {
        ActivityKind.Reply => Contract.ActivityKind.Reply,
        ActivityKind.Reaction => Contract.ActivityKind.Reaction,
        ActivityKind.Mention => Contract.ActivityKind.Mention,
        ActivityKind.MeetingInvite => Contract.ActivityKind.MeetingInvite,
        ActivityKind.MissedCall => Contract.ActivityKind.MissedCall,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static Contract.CallParticipantStatus ToContract(this CallParticipantStatus status) => status switch
    {
        CallParticipantStatus.Ringing => Contract.CallParticipantStatus.Ringing,
        CallParticipantStatus.Joined => Contract.CallParticipantStatus.Joined,
        CallParticipantStatus.Declined => Contract.CallParticipantStatus.Declined,
        CallParticipantStatus.Missed => Contract.CallParticipantStatus.Missed,
        CallParticipantStatus.Left => Contract.CallParticipantStatus.Left,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static Contract.ChannelRole ToContract(this ChannelRole role) => role switch
    {
        ChannelRole.Owner => Contract.ChannelRole.Owner,
        ChannelRole.Member => Contract.ChannelRole.Member,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static Contract.NotifyMode ToContract(this NotifyMode mode) => mode switch
    {
        NotifyMode.All => Contract.NotifyMode.All,
        NotifyMode.Mentions => Contract.NotifyMode.Mentions,
        NotifyMode.None => Contract.NotifyMode.None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static NotifyMode ToDomain(this Contract.NotifyMode mode) => mode switch
    {
        Contract.NotifyMode.All => NotifyMode.All,
        Contract.NotifyMode.Mentions => NotifyMode.Mentions,
        Contract.NotifyMode.None => NotifyMode.None,
        _ => throw AppException.Validation("Choose All, Mentions, or None."),
    };

    public static ChannelKind ToDomain(this Contract.ChannelKind kind) => kind switch
    {
        Contract.ChannelKind.Standard => ChannelKind.Standard,
        Contract.ChannelKind.Private => ChannelKind.Private,
        Contract.ChannelKind.Shared => ChannelKind.Shared,
        _ => throw AppException.Validation("Choose Standard or Private."),
    };

    public static Contract.InviteStatus ToContract(this InviteStatus status) => status switch
    {
        InviteStatus.Pending => Contract.InviteStatus.Pending,
        InviteStatus.Accepted => Contract.InviteStatus.Accepted,
        InviteStatus.Declined => Contract.InviteStatus.Declined,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static InviteStatus ToDomain(this Contract.InviteStatus status) => status switch
    {
        Contract.InviteStatus.Accepted => InviteStatus.Accepted,
        Contract.InviteStatus.Declined => InviteStatus.Declined,
        _ => throw AppException.Validation("Respond with Accepted or Declined."),
    };

    public static Contract.RecurrenceKind ToContract(this RecurrenceKind kind) => kind switch
    {
        RecurrenceKind.None => Contract.RecurrenceKind.None,
        RecurrenceKind.Daily => Contract.RecurrenceKind.Daily,
        RecurrenceKind.Weekly => Contract.RecurrenceKind.Weekly,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static RecurrenceKind ToDomain(this Contract.RecurrenceKind kind) => kind switch
    {
        Contract.RecurrenceKind.None => RecurrenceKind.None,
        Contract.RecurrenceKind.Daily => RecurrenceKind.Daily,
        Contract.RecurrenceKind.Weekly => RecurrenceKind.Weekly,
        _ => throw AppException.Validation("Choose None, Daily, or Weekly."),
    };

    public static Contract.RecordingStatus ToContract(this RecordingStatus status) => status switch
    {
        RecordingStatus.Unavailable => Contract.RecordingStatus.Unavailable,
        RecordingStatus.Starting => Contract.RecordingStatus.Starting,
        RecordingStatus.Recording => Contract.RecordingStatus.Recording,
        RecordingStatus.Ready => Contract.RecordingStatus.Ready,
        RecordingStatus.Failed => Contract.RecordingStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static Contract.PresenceStatus ToStatus(this Availability availability) => availability switch
    {
        Availability.Available => Contract.PresenceStatus.Available,
        Availability.Away => Contract.PresenceStatus.Away,
        Availability.Busy => Contract.PresenceStatus.Busy,
        Availability.DoNotDisturb => Contract.PresenceStatus.DoNotDisturb,
        _ => throw new ArgumentOutOfRangeException(nameof(availability), availability, null),
    };

    public static Availability ToAvailability(this Contract.PresenceStatus status) => status switch
    {
        Contract.PresenceStatus.Available => Availability.Available,
        Contract.PresenceStatus.Away => Availability.Away,
        Contract.PresenceStatus.Busy => Availability.Busy,
        Contract.PresenceStatus.DoNotDisturb => Availability.DoNotDisturb,
        _ => throw AppException.Validation("Choose Available, Away, Busy or Do not disturb."),
    };
}

internal static class Clock
{
    /// <summary>
    /// Current UTC time at microsecond precision, which is what PostgreSQL stores. Keeping values
    /// identical before and after a round trip lets clients use timestamps as paging cursors.
    /// </summary>
    public static DateTimeOffset Now(this TimeProvider time)
    {
        var now = time.GetUtcNow();
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>The conversation, channel, or meeting a message belongs to, with everyone who can see it.</summary>
internal sealed record MessageTarget(
    Guid OrganizationId,
    Conversation? Conversation,
    Channel? Channel,
    Meeting? Meeting,
    IReadOnlyList<Guid> MemberIds);

internal static class Access
{
    /// <summary>Loads the target and verifies the user is one of its members.</summary>
    public static async Task<MessageTarget> ResolveTargetAsync(
        this IAppDbContext db, Guid userId, Guid? conversationId, Guid? channelId, CancellationToken ct,
        Guid? meetingId = null)
    {
        var set = (conversationId is not null ? 1 : 0) + (channelId is not null ? 1 : 0) + (meetingId is not null ? 1 : 0);
        if (set != 1) throw AppException.Validation("Specify a conversation, channel, or meeting.");

        if (conversationId is { } cid)
        {
            var conversation = await db.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == cid, ct);
            if (conversation is null || conversation.Members.All(m => m.UserId != userId))
                throw AppException.NotFound("Conversation");
            return new MessageTarget(
                conversation.OrganizationId, conversation, null, null, conversation.Members.Select(m => m.UserId).ToList());
        }

        if (channelId is { } chid)
        {
            var channel = await db.Channels
                .Include(c => c.Members)
                .Include(c => c.Workspace).ThenInclude(w => w.Members)
                .FirstOrDefaultAsync(c => c.Id == chid, ct);
            if (channel is null) throw AppException.NotFound("Channel");

            if (channel.Kind == ChannelKind.Private)
            {
                if (channel.Members.All(m => m.UserId != userId)) throw AppException.NotFound("Channel");
                return new MessageTarget(
                    channel.Workspace.OrganizationId, null, channel, null, channel.Members.Select(m => m.UserId).ToList());
            }

            if (channel.Workspace.Members.All(m => m.UserId != userId)) throw AppException.NotFound("Channel");
            return new MessageTarget(
                channel.Workspace.OrganizationId, null, channel, null,
                channel.Workspace.Members.Select(m => m.UserId).ToList());
        }

        var meeting = await db.Meetings
            .Include(m => m.Participants)
            .Include(m => m.Invites)
            .FirstOrDefaultAsync(m => m.Id == meetingId, ct);
        if (meeting is null) throw AppException.NotFound("Meeting");
        var inOrg = await db.Memberships.AnyAsync(
            m => m.OrganizationId == meeting.OrganizationId && m.UserId == userId, ct);
        if (!inOrg) throw AppException.NotFound("Meeting");

        var memberIds = await db.Memberships
            .Where(m => m.OrganizationId == meeting.OrganizationId)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        return new MessageTarget(meeting.OrganizationId, null, null, meeting, memberIds);
    }

    public static async Task<Membership> RequireMembershipAsync(
        this IAppDbContext db, Guid userId, Guid organizationId, CancellationToken ct)
    {
        return await db.Memberships.FirstOrDefaultAsync(
                   m => m.OrganizationId == organizationId && m.UserId == userId, ct)
               ?? throw AppException.NotFound("Organization");
    }

    /// <summary>Everyone who shares at least one organization with the user, including the user.</summary>
    public static async Task<List<Guid>> GetColleagueIdsAsync(this IAppDbContext db, Guid userId, CancellationToken ct)
    {
        var orgIds = db.Memberships.Where(m => m.UserId == userId).Select(m => m.OrganizationId);
        var ids = await db.Memberships
            .Where(m => orgIds.Contains(m.OrganizationId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
        if (!ids.Contains(userId)) ids.Add(userId);
        return ids;
    }
}
