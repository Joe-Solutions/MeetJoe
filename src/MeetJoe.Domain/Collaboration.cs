namespace MeetJoe.Domain;

/// <summary>A team: a group of people with a set of channels.</summary>
public class Workspace
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 280;
    public const string DefaultChannelName = "General";

    private Workspace() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid CreatedById { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public List<WorkspaceMember> Members { get; private set; } = [];
    public List<Channel> Channels { get; private set; } = [];

    public static Workspace Create(
        Guid organizationId, Guid creatorId, string name, string? description, DateTimeOffset now)
    {
        var workspace = new Workspace
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Name = Guard.Required(name, "Workspace name", MaxNameLength),
            Description = Guard.Optional(description, "Description", MaxDescriptionLength),
            CreatedById = creatorId,
            CreatedAt = now,
        };
        workspace.Members.Add(new WorkspaceMember(workspace.Id, creatorId, WorkspaceRole.Owner, now));
        workspace.Channels.Add(Channel.CreateStandard(workspace.Id, DefaultChannelName, null, now));
        return workspace;
    }
}

public class WorkspaceMember(Guid workspaceId, Guid userId, WorkspaceRole role, DateTimeOffset joinedAt)
{
    public Guid WorkspaceId { get; private set; } = workspaceId;
    public Guid UserId { get; private set; } = userId;
    public WorkspaceRole Role { get; private set; } = role;
    public DateTimeOffset JoinedAt { get; private set; } = joinedAt;

    public Workspace Workspace { get; private set; } = null!;
    public User User { get; private set; } = null!;
}

public class Channel
{
    public const int MaxNameLength = 50;
    public const int MaxDescriptionLength = 280;

    private Channel() { }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Lower-cased name; unique within the workspace.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public string? Description { get; private set; }
    public ChannelKind Kind { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public Workspace Workspace { get; private set; } = null!;
    public List<ChannelMember> Members { get; private set; } = [];

    public bool IsPrivate => Kind == ChannelKind.Private;

    public static Channel CreateStandard(Guid workspaceId, string name, string? description, DateTimeOffset now) =>
        Create(workspaceId, name, description, ChannelKind.Standard, now);

    /// <summary>Private channel: only listed members can see or post. Creator becomes owner.</summary>
    public static Channel CreatePrivate(
        Guid workspaceId, Guid creatorId, string name, string? description,
        IEnumerable<Guid> otherMemberIds, DateTimeOffset now)
    {
        var channel = Create(workspaceId, name, description, ChannelKind.Private, now);
        channel.Members.Add(new ChannelMember(channel.Id, creatorId, ChannelRole.Owner, now));
        foreach (var id in otherMemberIds.Where(id => id != creatorId).Distinct())
            channel.Members.Add(new ChannelMember(channel.Id, id, ChannelRole.Member, now));
        return channel;
    }

    /// <summary>Backward-compatible factory used by existing call sites; creates a standard channel.</summary>
    public static Channel Create(Guid workspaceId, string name, string? description, DateTimeOffset now) =>
        CreateStandard(workspaceId, name, description, now);

    private static Channel Create(
        Guid workspaceId, string name, string? description, ChannelKind kind, DateTimeOffset now)
    {
        if (kind == ChannelKind.Shared)
            throw new DomainException("Shared channels are not available yet.");

        var cleanName = Guard.Required(name, "Channel name", MaxNameLength);
        return new Channel
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            Name = cleanName,
            NormalizedName = NormalizeName(cleanName),
            Description = Guard.Optional(description, "Description", MaxDescriptionLength),
            Kind = kind,
            CreatedAt = now,
        };
    }

    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    public ChannelMember AddMember(Guid userId, ChannelRole role, DateTimeOffset now)
    {
        if (!IsPrivate) throw new DomainException("Only private channels have their own membership list.");
        if (Members.Any(m => m.UserId == userId))
            throw new DomainException("That person is already in this channel.");
        var member = new ChannelMember(Id, userId, role, now);
        Members.Add(member);
        return member;
    }

    public void RemoveMember(Guid userId)
    {
        if (!IsPrivate) throw new DomainException("Only private channels have their own membership list.");
        var member = Members.FirstOrDefault(m => m.UserId == userId)
                     ?? throw new DomainException("That person is not in this channel.");
        if (member.Role == ChannelRole.Owner && Members.Count(m => m.Role == ChannelRole.Owner) == 1)
            throw new DomainException("Add another owner before removing the last one.");
        Members.Remove(member);
    }
}

public class ChannelMember
{
    private ChannelMember() { }

    public ChannelMember(Guid channelId, Guid userId, ChannelRole role, DateTimeOffset joinedAt)
    {
        ChannelId = channelId;
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
        // One tick before join so same-instant posts still count as unread for other members.
        LastReadAt = joinedAt.AddTicks(-1);
    }

    public Guid ChannelId { get; private set; }
    public Guid UserId { get; private set; }
    public ChannelRole Role { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
    public DateTimeOffset LastReadAt { get; private set; }
    public bool IsMuted { get; private set; }
    public NotifyMode NotifyMode { get; private set; } = NotifyMode.All;

    public Channel Channel { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public void MarkRead(DateTimeOffset at)
    {
        if (at > LastReadAt) LastReadAt = at;
    }

    public void SetPreferences(bool isMuted, NotifyMode notifyMode)
    {
        IsMuted = isMuted;
        NotifyMode = notifyMode;
    }
}

public class Meeting
{
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 2000;

    private Meeting() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid HostId { get; private set; }

    /// <summary>Name of the media room on the SFU. Entry is granted by a signed token, not by knowing the name.</summary>
    public string RoomName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public bool LobbyEnabled { get; private set; }

    public Guid? SeriesId { get; private set; }
    public RecurrenceKind Recurrence { get; private set; }
    public DateTimeOffset? RecurrenceUntil { get; private set; }

    public User Host { get; private set; } = null!;
    public List<MeetingParticipant> Participants { get; private set; } = [];
    public List<MeetingInvite> Invites { get; private set; } = [];
    public List<MeetingRecording> Recordings { get; private set; } = [];

    public bool IsActive => EndedAt is null;
    public bool IsScheduled => StartsAt > CreatedAt.AddMinutes(1);

    public static Meeting Create(Guid organizationId, Guid hostId, string title, DateTimeOffset now) =>
        Schedule(organizationId, hostId, title, null, now, null, lobbyEnabled: false, inviteeIds: [], now,
            RecurrenceKind.None, null);

    public static Meeting Schedule(
        Guid organizationId,
        Guid hostId,
        string title,
        string? description,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt,
        bool lobbyEnabled,
        IEnumerable<Guid> inviteeIds,
        DateTimeOffset now,
        RecurrenceKind recurrence = RecurrenceKind.None,
        DateTimeOffset? recurrenceUntil = null,
        Guid? seriesId = null)
    {
        if (endsAt is { } end && end <= startsAt)
            throw new DomainException("A meeting must end after it starts.");
        if (recurrence != RecurrenceKind.None && recurrenceUntil is { } until && until < startsAt)
            throw new DomainException("Recurrence must end on or after the first meeting.");

        var id = Guid.CreateVersion7();
        var meeting = new Meeting
        {
            Id = id,
            OrganizationId = organizationId,
            Title = Guard.Required(title, "Meeting title", MaxTitleLength),
            Description = Guard.Optional(description, "Description", MaxDescriptionLength),
            HostId = hostId,
            RoomName = $"mj-{id:N}",
            CreatedAt = now,
            StartsAt = startsAt,
            EndsAt = endsAt,
            LobbyEnabled = lobbyEnabled,
            SeriesId = seriesId ?? (recurrence == RecurrenceKind.None ? null : Guid.CreateVersion7()),
            Recurrence = recurrence,
            RecurrenceUntil = recurrence == RecurrenceKind.None ? null : recurrenceUntil,
        };

        foreach (var inviteeId in inviteeIds.Where(id => id != hostId).Distinct())
            meeting.Invites.Add(new MeetingInvite(meeting.Id, inviteeId, now));

        return meeting;
    }

    public void End(DateTimeOffset now)
    {
        if (!IsActive) return;
        EndedAt = now;
        foreach (var participant in Participants) participant.Leave(now);
    }

    public MeetingInvite? RespondToInvite(Guid userId, InviteStatus status, DateTimeOffset now)
    {
        if (status is not (InviteStatus.Accepted or InviteStatus.Declined))
            throw new DomainException("Respond with Accepted or Declined.");
        var invite = Invites.FirstOrDefault(i => i.UserId == userId)
                     ?? throw new DomainException("You were not invited to this meeting.");
        invite.Respond(status, now);
        return invite;
    }

    /// <summary>Adds pending invites for people who are not already invited or in the room.</summary>
    public IReadOnlyList<MeetingInvite> InvitePeople(IEnumerable<Guid> userIds, DateTimeOffset now)
    {
        if (!IsActive) throw new DomainException("This meeting has ended.");

        var added = new List<MeetingInvite>();
        foreach (var userId in userIds.Where(id => id != HostId).Distinct())
        {
            if (Invites.Any(i => i.UserId == userId)) continue;
            if (Participants.Any(p => p.UserId == userId && p.IsPresent)) continue;

            var invite = new MeetingInvite(Id, userId, now);
            Invites.Add(invite);
            added.Add(invite);
        }

        return added;
    }

    /// <summary>Expands the next occurrences for a recurring series (not including this instance).</summary>
    public IEnumerable<DateTimeOffset> NextOccurrences(int count)
    {
        if (Recurrence == RecurrenceKind.None || count <= 0) yield break;
        var cursor = StartsAt;
        for (var i = 0; i < count; i++)
        {
            cursor = Recurrence == RecurrenceKind.Daily ? cursor.AddDays(1) : cursor.AddDays(7);
            if (RecurrenceUntil is { } until && cursor > until) yield break;
            yield return cursor;
        }
    }
}

public class MeetingParticipant
{
    private MeetingParticipant() { }

    public MeetingParticipant(Guid meetingId, Guid userId, DateTimeOffset joinedAt, bool inLobby = false)
    {
        Id = Guid.CreateVersion7();
        MeetingId = meetingId;
        UserId = userId;
        JoinedAt = joinedAt;
        InLobby = inLobby;
    }

    public Guid Id { get; private set; }
    public Guid MeetingId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
    public DateTimeOffset? LeftAt { get; private set; }
    public bool InLobby { get; private set; }
    public bool HandRaised { get; private set; }

    public User User { get; private set; } = null!;

    public bool IsPresent => LeftAt is null;

    public void Leave(DateTimeOffset now)
    {
        LeftAt ??= now;
        HandRaised = false;
        InLobby = false;
    }

    public void Admit()
    {
        if (!IsPresent) throw new DomainException("That person has left the meeting.");
        InLobby = false;
    }

    public void Reject(DateTimeOffset now) => Leave(now);

    public void SetHandRaised(bool raised)
    {
        if (!IsPresent || InLobby) throw new DomainException("Join the meeting before raising a hand.");
        HandRaised = raised;
    }
}

public class MeetingInvite(Guid meetingId, Guid userId, DateTimeOffset invitedAt)
{
    public Guid MeetingId { get; private set; } = meetingId;
    public Guid UserId { get; private set; } = userId;
    public InviteStatus Status { get; private set; } = InviteStatus.Pending;
    public DateTimeOffset InvitedAt { get; private set; } = invitedAt;
    public DateTimeOffset? RespondedAt { get; private set; }

    public User User { get; private set; } = null!;

    public void Respond(InviteStatus status, DateTimeOffset now)
    {
        Status = status;
        RespondedAt = now;
    }
}

/// <summary>Recording metadata for a meeting (client-side capture uploads the finished file).</summary>
public class MeetingRecording
{
    private MeetingRecording() { }

    public Guid Id { get; private set; }
    public Guid MeetingId { get; private set; }
    public Guid StartedById { get; private set; }
    public RecordingStatus Status { get; private set; }
    public string? StorageKey { get; private set; }
    public string? FileName { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public bool IsInProgress =>
        Status is RecordingStatus.Starting or RecordingStatus.Recording;

    public static MeetingRecording Start(Guid meetingId, Guid startedById, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        MeetingId = meetingId,
        StartedById = startedById,
        Status = RecordingStatus.Recording,
        StartedAt = now,
        EndedAt = null,
        FileName = null,
        StorageKey = null,
    };

    public void MarkReady(string storageKey, string fileName, DateTimeOffset now)
    {
        if (!IsInProgress) throw new DomainException("This recording is not in progress.");
        Status = RecordingStatus.Ready;
        StorageKey = Guard.Required(storageKey, "Storage key", 100);
        FileName = Guard.Required(fileName, "File name", MaxFileNameLength);
        EndedAt = now;
    }

    public void MarkFailed(DateTimeOffset now)
    {
        if (!IsInProgress) return;
        Status = RecordingStatus.Failed;
        EndedAt = now;
    }

    public const int MaxFileNameLength = 255;
}
