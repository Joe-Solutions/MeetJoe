namespace MeetJoe.Domain;

/// <summary>An instant VoIP session started from a chat conversation.</summary>
public class Call
{
    /// <summary>How long a call may ring with nobody answering before it is treated as missed.</summary>
    public static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(30);

    private Call() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid InitiatorId { get; private set; }
    public CallKind Kind { get; private set; }
    public string RoomName { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public User Initiator { get; private set; } = null!;
    public Conversation Conversation { get; private set; } = null!;
    public List<CallParticipant> Participants { get; private set; } = [];

    public bool IsActive => EndedAt is null;
    public bool IsConnected => ConnectedAt is not null && EndedAt is null;

    /// <summary>True when the call never connected and has been ringing longer than <see cref="RingTimeout"/>.</summary>
    public bool IsUnansweredRingExpired(DateTimeOffset now) =>
        IsActive && ConnectedAt is null && now - CreatedAt >= RingTimeout;

    public static Call Start(
        Guid organizationId,
        Guid conversationId,
        Guid initiatorId,
        IReadOnlyCollection<Guid> otherMemberIds,
        DateTimeOffset now)
    {
        var others = otherMemberIds.Where(id => id != initiatorId).Distinct().ToList();
        if (others.Count == 0) throw new DomainException("A call needs at least one other person.");

        var id = Guid.CreateVersion7();
        var call = new Call
        {
            Id = id,
            OrganizationId = organizationId,
            ConversationId = conversationId,
            InitiatorId = initiatorId,
            Kind = others.Count == 1 ? CallKind.Direct : CallKind.Group,
            RoomName = $"call-{id:N}",
            CreatedAt = now,
        };
        call.Participants.Add(CallParticipant.CreateJoined(id, initiatorId, now));
        foreach (var userId in others)
            call.Participants.Add(CallParticipant.CreateRinging(id, userId, now));
        return call;
    }

    public void MarkConnected(DateTimeOffset now) => ConnectedAt ??= now;

    /// <summary>Rings additional people into this live call; promotes Direct → Group when needed.</summary>
    public IReadOnlyList<Guid> Invite(IReadOnlyCollection<Guid> userIds, DateTimeOffset now)
    {
        if (!IsActive) throw new DomainException("This call has ended.");

        var added = new List<Guid>();
        foreach (var userId in userIds.Where(id => id != Guid.Empty).Distinct())
        {
            if (Participants.Any(p => p.UserId == userId)) continue;
            Participants.Add(CallParticipant.CreateRinging(Id, userId, now));
            added.Add(userId);
        }

        if (added.Count > 0 && Kind == CallKind.Direct)
            Kind = CallKind.Group;

        return added;
    }

    public void End(DateTimeOffset now)
    {
        if (!IsActive) return;
        EndedAt = now;
        foreach (var participant in Participants.Where(p => p.Status is CallParticipantStatus.Ringing or CallParticipantStatus.Joined))
        {
            if (participant.Status == CallParticipantStatus.Ringing) participant.MarkMissed(now);
            else participant.Leave(now);
        }
    }
}

public class CallParticipant
{
    private CallParticipant() { }

    public Guid CallId { get; private set; }
    public Guid UserId { get; private set; }
    public CallParticipantStatus Status { get; private set; }
    public DateTimeOffset InvitedAt { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public DateTimeOffset? LeftAt { get; private set; }

    public Call Call { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public static CallParticipant CreateJoined(Guid callId, Guid userId, DateTimeOffset now) => new()
    {
        CallId = callId,
        UserId = userId,
        Status = CallParticipantStatus.Joined,
        InvitedAt = now,
        AnsweredAt = now,
    };

    public static CallParticipant CreateRinging(Guid callId, Guid userId, DateTimeOffset now) => new()
    {
        CallId = callId,
        UserId = userId,
        Status = CallParticipantStatus.Ringing,
        InvitedAt = now,
    };

    public void Answer(DateTimeOffset now)
    {
        if (Status != CallParticipantStatus.Ringing)
            throw new DomainException("That call invitation is no longer ringing.");
        Status = CallParticipantStatus.Joined;
        AnsweredAt = now;
    }

    public void Decline(DateTimeOffset now)
    {
        if (Status != CallParticipantStatus.Ringing)
            throw new DomainException("That call invitation is no longer ringing.");
        Status = CallParticipantStatus.Declined;
        LeftAt = now;
    }

    public void MarkMissed(DateTimeOffset now)
    {
        if (Status != CallParticipantStatus.Ringing) return;
        Status = CallParticipantStatus.Missed;
        LeftAt = now;
    }

    public void Leave(DateTimeOffset now)
    {
        if (Status != CallParticipantStatus.Joined) return;
        Status = CallParticipantStatus.Left;
        LeftAt = now;
    }
}
