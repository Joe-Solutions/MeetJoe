using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Calls;

/// <summary>Instant VoIP from chat: ring, answer, decline, hang up, history, missed-call activity.</summary>
public sealed class CallService(
    IAppDbContext db, IMeetingTokenIssuer tokens, IRealtimePublisher realtime, TimeProvider time)
{
    public const int HistoryLimit = 50;

    public async Task<IReadOnlyList<CallDto>> GetHistoryAsync(
        Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);
        var calls = await db.Calls.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Participants.Any(p => p.UserId == userId))
            .Include(c => c.Initiator)
            .Include(c => c.Participants).ThenInclude(p => p.User)
            .OrderByDescending(c => c.CreatedAt)
            .Take(HistoryLimit)
            .ToListAsync(ct);
        return calls.Select(ToDto).ToList();
    }

    public async Task<CallJoinDto> StartAsync(
        Guid userId, Guid organizationId, StartCallRequest request, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var conversation = await db.Conversations.Include(c => c.Members)
                               .FirstOrDefaultAsync(c => c.Id == request.ConversationId, ct)
                           ?? throw AppException.NotFound("Conversation");
        if (conversation.OrganizationId != organizationId)
            throw AppException.NotFound("Conversation");
        if (conversation.Members.All(m => m.UserId != userId))
            throw AppException.NotFound("Conversation");

        var active = await db.Calls.AnyAsync(
            c => c.ConversationId == conversation.Id && c.EndedAt == null, ct);
        if (active) throw AppException.Conflict("There is already an active call in this chat.");

        var now = time.Now();
        var others = conversation.Members.Select(m => m.UserId).Where(id => id != userId).ToList();
        var call = Domain.Call.Start(organizationId, conversation.Id, userId, others, now);
        db.Calls.Add(call);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(await LoadAsync(call.Id, ct));
        var callees = others;
        await realtime.IncomingCallAsync(callees, dto, ct);
        await realtime.CallUpdatedAsync(conversation.Members.Select(m => m.UserId).ToList(), dto, ct);
        await realtime.CallsChangedAsync(conversation.Members.Select(m => m.UserId).ToList(), ct);

        return await IssueJoinAsync(userId, call, ct);
    }

    public async Task<CallJoinDto> AnswerAsync(Guid userId, Guid callId, CancellationToken ct = default)
    {
        var call = await RequireParticipantAsync(userId, callId, ct);
        if (!call.IsActive) throw AppException.Conflict("This call has ended.");

        var now = time.Now();
        var me = call.Participants.First(p => p.UserId == userId);
        me.Answer(now);
        call.MarkConnected(now);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(await LoadAsync(call.Id, ct));
        await NotifyParticipantsAsync(call, dto, ct);
        return await IssueJoinAsync(userId, call, ct);
    }

    public async Task DeclineAsync(Guid userId, Guid callId, CancellationToken ct = default)
    {
        var call = await RequireParticipantAsync(userId, callId, ct);
        if (!call.IsActive) return;

        var now = time.Now();
        call.Participants.First(p => p.UserId == userId).Decline(now);

        // Direct call ends when the only callee declines.
        if (call.Kind == Domain.CallKind.Direct
            || call.Participants.Where(p => p.UserId != call.InitiatorId)
                .All(p => p.Status is Domain.CallParticipantStatus.Declined or Domain.CallParticipantStatus.Missed))
        {
            call.End(now);
        }

        await db.SaveChangesAsync(ct);
        var dto = ToDto(await LoadAsync(call.Id, ct));
        await NotifyParticipantsAsync(call, dto, ct);
    }

    public async Task HangUpAsync(Guid userId, Guid callId, CancellationToken ct = default)
    {
        var call = await RequireParticipantAsync(userId, callId, ct);
        if (!call.IsActive) return;

        var now = time.Now();
        var me = call.Participants.First(p => p.UserId == userId);
        if (me.Status == Domain.CallParticipantStatus.Joined) me.Leave(now);

        var stillJoined = call.Participants.Count(p => p.Status == Domain.CallParticipantStatus.Joined);
        var stillRinging = call.Participants.Any(p => p.Status == Domain.CallParticipantStatus.Ringing);
        if (userId == call.InitiatorId || stillJoined == 0 || (call.Kind == Domain.CallKind.Direct && !stillRinging && stillJoined <= 1))
            await EndInternalAsync(call, now, ct);
        else
        {
            await db.SaveChangesAsync(ct);
            var dto = ToDto(await LoadAsync(call.Id, ct));
            await NotifyParticipantsAsync(call, dto, ct);
        }
    }

    public async Task EndAsync(Guid userId, Guid callId, CancellationToken ct = default)
    {
        var call = await RequireParticipantAsync(userId, callId, ct);
        if (call.InitiatorId != userId && call.Participants.All(p => p.UserId != userId))
            throw AppException.Forbidden("Only a participant can end this call.");
        if (!call.IsActive) return;
        await EndInternalAsync(call, time.Now(), ct);
    }

    /// <summary>Invite org members into an active call; they receive an incoming ring.</summary>
    public async Task<CallDto> InviteAsync(
        Guid userId, Guid callId, InviteToCallRequest request, CancellationToken ct = default)
    {
        var call = await RequireParticipantAsync(userId, callId, ct);
        if (!call.IsActive) throw AppException.Conflict("This call has ended.");

        var me = call.Participants.First(p => p.UserId == userId);
        if (me.Status is not Domain.CallParticipantStatus.Joined)
            throw AppException.Forbidden("Join the call before inviting others.");

        var invitees = (request.UserIds ?? [])
            .Where(id => id != Guid.Empty && id != userId)
            .Distinct()
            .ToList();
        if (invitees.Count == 0) throw AppException.Validation("Pick at least one person.");

        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == call.OrganizationId && invitees.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(ct);
        if (members.Count != invitees.Count)
            throw AppException.NotFound("One or more people are not in this organization.");

        var now = time.Now();
        var added = call.Invite(members, now);
        if (added.Count == 0) throw AppException.Conflict("Those people are already on this call.");

        await db.SaveChangesAsync(ct);
        var dto = ToDto(await LoadAsync(call.Id, ct));
        await realtime.IncomingCallAsync(added, dto, ct);
        await NotifyParticipantsAsync(call, dto, ct);
        return dto;
    }

    /// <summary>
    /// Ends calls that rang with no answer past <see cref="Domain.Call.RingTimeout"/>.
    /// Used by the background worker so a stuck ring cannot block the next call.
    /// </summary>
    public async Task<int> ExpireUnansweredRingsAsync(CancellationToken ct = default)
    {
        var now = time.Now();
        var cutoff = now - Domain.Call.RingTimeout;
        var expired = await db.Calls
            .Include(c => c.Participants)
            .Where(c => c.EndedAt == null && c.ConnectedAt == null && c.CreatedAt <= cutoff)
            .ToListAsync(ct);

        foreach (var call in expired)
            await EndInternalAsync(call, now, ct);

        return expired.Count;
    }

    private async Task EndInternalAsync(Domain.Call call, DateTimeOffset now, CancellationToken ct)
    {
        var missed = call.Participants
            .Where(p => p.Status == Domain.CallParticipantStatus.Ringing)
            .Select(p => p.UserId)
            .ToList();

        call.End(now);

        if (missed.Count > 0)
        {
            var actorName = await db.Users.Where(u => u.Id == call.InitiatorId).Select(u => u.DisplayName).FirstAsync(ct);
            foreach (var recipientId in missed)
            {
                db.ActivityItems.Add(Domain.ActivityItem.Create(
                    call.OrganizationId, recipientId, call.InitiatorId, Domain.ActivityKind.MissedCall,
                    null, call.ConversationId, null,
                    $"Missed call from {actorName}", now, callId: call.Id));
            }
        }

        await db.SaveChangesAsync(ct);
        var dto = ToDto(await LoadAsync(call.Id, ct));
        await NotifyParticipantsAsync(call, dto, ct);
        if (missed.Count > 0) await realtime.ActivityChangedAsync(missed, ct);
    }

    private async Task<CallJoinDto> IssueJoinAsync(Guid userId, Domain.Call call, CancellationToken ct)
    {
        var displayName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
        var connection = tokens.Issue(call.RoomName, userId, displayName, canPublish: true, roomAdmin: userId == call.InitiatorId);
        return new CallJoinDto(call.Id, connection.ServerUrl, call.RoomName, connection.Token);
    }

    private async Task NotifyParticipantsAsync(Domain.Call call, CallDto dto, CancellationToken ct)
    {
        var ids = call.Participants.Select(p => p.UserId).ToList();
        await realtime.CallUpdatedAsync(ids, dto, ct);
        await realtime.CallsChangedAsync(ids, ct);
    }

    private async Task<Domain.Call> RequireParticipantAsync(Guid userId, Guid callId, CancellationToken ct)
    {
        var call = await db.Calls.Include(c => c.Participants).FirstOrDefaultAsync(c => c.Id == callId, ct)
                   ?? throw AppException.NotFound("Call");
        if (call.Participants.All(p => p.UserId != userId)) throw AppException.NotFound("Call");
        return call;
    }

    private async Task<Domain.Call> LoadAsync(Guid callId, CancellationToken ct) =>
        await db.Calls.AsNoTracking()
            .Include(c => c.Initiator)
            .Include(c => c.Participants).ThenInclude(p => p.User)
            .FirstAsync(c => c.Id == callId, ct);

    private static CallDto ToDto(Domain.Call call) =>
        new(call.Id,
            call.ConversationId,
            call.Kind == Domain.CallKind.Direct ? CallKind.Direct : CallKind.Group,
            new UserSummaryDto(call.InitiatorId, call.Initiator.DisplayName),
            call.CreatedAt,
            call.ConnectedAt,
            call.EndedAt,
            call.IsActive,
            call.Participants
                .OrderBy(p => p.InvitedAt)
                .Select(p => new CallParticipantDto(p.UserId, p.User.DisplayName, p.Status.ToContract()))
                .ToList());
}
