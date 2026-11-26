using MeetJoe.Application.Chat;
using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Meetings;

/// <summary>
/// Meeting lifecycle and media credentials. Who is "in" a meeting is tracked from join and leave
/// calls, so a client that exits without leaving stays listed until the meeting ends.
/// </summary>
public sealed class MeetingService(
    IAppDbContext db,
    IMeetingTokenIssuer tokens,
    IRealtimePublisher realtime,
    TimeProvider time,
    ChatService chat,
    IEmailSender email,
    IGuestAccountService guests,
    Microsoft.Extensions.Options.IOptions<PublicUrlsOptions> publicUrls)
{
    private readonly PublicUrlsOptions _urls = publicUrls.Value;
    private const int RecurrenceExpandCount = 8;

    public async Task<IReadOnlyList<MeetingDto>> GetActiveAsync(
        Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);
        var meetings = await db.Meetings.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.EndedAt == null)
            .Include(m => m.Host)
            .Include(m => m.Participants).ThenInclude(p => p.User)
            .Include(m => m.Invites).ThenInclude(i => i.User)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
        // Instant rooms and anything currently joined; future-only scheduled items live on the calendar.
        return meetings
            .Where(m => !m.IsScheduled || m.Participants.Any(p => p.IsPresent))
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<MeetingDto>> GetCalendarAsync(
        Guid userId, Guid organizationId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);
        if (to <= from) throw AppException.Validation("Calendar range must end after it starts.");

        var meetings = await db.Meetings.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId
                        && m.StartsAt < to
                        && (m.EndsAt == null || m.EndsAt > from))
            .Include(m => m.Host)
            .Include(m => m.Participants).ThenInclude(p => p.User)
            .Include(m => m.Invites).ThenInclude(i => i.User)
            .OrderBy(m => m.StartsAt)
            .ToListAsync(ct);
        return meetings.Select(ToDto).ToList();
    }

    public async Task<MeetingDto> CreateAsync(
        Guid userId, Guid organizationId, CreateMeetingRequest request, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var meeting = Domain.Meeting.Create(organizationId, userId, request.Title, time.Now());
        db.Meetings.Add(meeting);
        await db.SaveChangesAsync(ct);

        await NotifyAsync(organizationId, ct);
        return ToDto(await LoadAsync(meeting.Id, ct));
    }

    public async Task<MeetingDto> ScheduleAsync(
        Guid userId, Guid organizationId, ScheduleMeetingRequest request, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var invitees = (request.InviteeIds ?? []).Where(id => id != userId).Distinct().ToList();
        if (invitees.Count > 0)
        {
            var known = await db.Memberships.CountAsync(
                m => m.OrganizationId == organizationId && invitees.Contains(m.UserId), ct);
            if (known != invitees.Count)
                throw AppException.Validation("Everyone invited must belong to the organization.");
        }

        var hostEmail = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstAsync(ct);
        var emailInvitees = NormalizeEmails(request.InviteeEmails)
            .Where(e => !string.Equals(e, hostEmail, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Map org-member emails onto InviteeIds so they get in-app + email invites.
        if (emailInvitees.Count > 0)
        {
            var membersByEmail = await db.Memberships
                .Where(m => m.OrganizationId == organizationId)
                .Select(m => new { m.UserId, m.User.Email })
                .ToListAsync(ct);
            foreach (var row in membersByEmail)
            {
                if (row.Email is null) continue;
                if (!emailInvitees.Contains(row.Email, StringComparer.OrdinalIgnoreCase)) continue;
                if (row.UserId != userId && !invitees.Contains(row.UserId))
                    invitees.Add(row.UserId);
            }
        }

        var now = time.Now();
        var recurrence = request.Recurrence.ToDomain();
        var primary = Domain.Meeting.Schedule(
            organizationId, userId, request.Title, request.Description, request.StartsAt, request.EndsAt,
            request.LobbyEnabled, invitees, now, recurrence, request.RecurrenceUntil);

        // External (or unresolved) emails become guest invites with join tokens.
        var memberEmails = await db.Users
            .Where(u => invitees.Contains(u.Id))
            .Select(u => u.Email!)
            .ToListAsync(ct);
        var guestEmails = emailInvitees
            .Where(e => memberEmails.All(m => !string.Equals(m, e, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var guestEmail in guestEmails)
            primary.AddGuestInvite(guestEmail, null, now);

        db.Meetings.Add(primary);

        var duration = request.EndsAt is { } end ? end - request.StartsAt : (TimeSpan?)null;
        foreach (var startsAt in primary.NextOccurrences(RecurrenceExpandCount))
        {
            var endsAt = duration is { } d ? startsAt + d : (DateTimeOffset?)null;
            var occurrence = Domain.Meeting.Schedule(
                organizationId, userId, request.Title, request.Description, startsAt, endsAt,
                request.LobbyEnabled, invitees, now, Domain.RecurrenceKind.None, null, primary.SeriesId);
            foreach (var guestEmail in guestEmails)
                occurrence.AddGuestInvite(guestEmail, null, now);
            db.Meetings.Add(occurrence);
        }

        var actorName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
        foreach (var inviteeId in invitees)
        {
            db.ActivityItems.Add(Domain.ActivityItem.Create(
                organizationId, inviteeId, userId, Domain.ActivityKind.MeetingInvite,
                null, null, null, $"{actorName} invited you to “{primary.Title}”", now, primary.Id));
        }

        await db.SaveChangesAsync(ct);
        await NotifyAsync(organizationId, ct);
        if (invitees.Count > 0) await realtime.ActivityChangedAsync(invitees, ct);

        await SendScheduleInviteEmailsAsync(primary.Id, actorName, ct);
        return ToDto(await LoadAsync(primary.Id, ct));
    }

    public async Task<GuestMeetingInviteDto> GetGuestInviteAsync(string token, CancellationToken ct = default)
    {
        var (meeting, inviteEmail, suggestedName, inviteToken) = await ResolveInviteTokenAsync(token, ct);
        return MeetingInviteMail.ToPublicDto(
            inviteToken, meeting, meeting.Host.DisplayName, inviteEmail, suggestedName, _urls);
    }

    public async Task<GuestJoinDto> JoinAsGuestAsync(
        string token, GuestJoinRequest request, CancellationToken ct = default)
    {
        var (meeting, inviteEmail, _, inviteToken) = await ResolveInviteTokenAsync(token, ct);
        if (!meeting.IsActive) throw AppException.Conflict("This meeting has ended.");

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? inviteEmail?.Split('@')[0] ?? "Guest"
            : request.DisplayName.Trim();
        if (displayName.Length > Domain.User.MaxDisplayNameLength)
            displayName = displayName[..Domain.User.MaxDisplayNameLength];

        var email = inviteEmail
                    ?? throw AppException.Validation("This invite is missing an email address.");
        var user = await guests.GetOrCreateGuestAsync(email, displayName, ct);

        // Member invites: mark accepted when they join from the link.
        var memberInvite = meeting.Invites.FirstOrDefault(i => i.InviteToken == inviteToken);
        if (memberInvite is not null && memberInvite.Status == Domain.InviteStatus.Pending)
            memberInvite.Respond(Domain.InviteStatus.Accepted, time.Now());

        var guestInvite = meeting.GuestInvites.FirstOrDefault(i => i.InviteToken == inviteToken);
        guestInvite?.SetDisplayName(displayName);
        guestInvite?.MarkJoined(user.Id, time.Now());

        var now = time.Now();
        var isHost = meeting.HostId == user.Id;
        // Email/web guests need mic and camera immediately — do not park them in the lobby
        // with a non-publish token (that surfaces as a false "permission" error in the browser).
        const bool inLobby = false;
        var existing = meeting.Participants.FirstOrDefault(p => p.UserId == user.Id && p.IsPresent);
        if (existing is null)
        {
            db.MeetingParticipants.Add(new Domain.MeetingParticipant(meeting.Id, user.Id, now, inLobby));
        }
        else if (existing.InLobby)
        {
            existing.Admit();
        }

        await db.SaveChangesAsync(ct);
        await NotifyAsync(meeting.OrganizationId, ct);
        await PublishMeetingAsync(meeting.Id, ct);

        var connection = tokens.Issue(
            meeting.RoomName, user.Id, displayName,
            canPublish: true,
            roomAdmin: isHost);
        return new GuestJoinDto(
            meeting.Id, connection.ServerUrl, meeting.RoomName, connection.Token, InLobby: false, displayName, user.Id);
    }

    public async Task<MeetingJoinDto> JoinAsync(Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        if (!meeting.IsActive) throw AppException.Conflict("This meeting has ended.");

        var now = time.Now();
        var isHost = meeting.HostId == userId;
        var inLobby = meeting.LobbyEnabled && !isHost;
        var existing = meeting.Participants.FirstOrDefault(p => p.UserId == userId && p.IsPresent);

        if (existing is null)
        {
            db.MeetingParticipants.Add(new Domain.MeetingParticipant(meeting.Id, userId, now, inLobby));
            await db.SaveChangesAsync(ct);
            await NotifyAsync(meeting.OrganizationId, ct);
            await PublishMeetingAsync(meeting.Id, ct);
        }
        else if (existing.InLobby && isHost)
        {
            existing.Admit();
            await db.SaveChangesAsync(ct);
            await NotifyAsync(meeting.OrganizationId, ct);
            await PublishMeetingAsync(meeting.Id, ct);
            inLobby = false;
        }
        else
        {
            inLobby = existing.InLobby;
        }

        var displayName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
        var connection = tokens.Issue(
            meeting.RoomName, userId, displayName,
            canPublish: !inLobby,
            roomAdmin: isHost);
        return new MeetingJoinDto(meeting.Id, connection.ServerUrl, meeting.RoomName, connection.Token, inLobby);
    }

    public async Task LeaveAsync(Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        var now = time.Now();

        var mine = meeting.Participants.Where(p => p.UserId == userId && p.IsPresent).ToList();
        if (mine.Count == 0) return;
        foreach (var participant in mine) participant.Leave(now);

        // A meeting with nobody left in it is over (instant meetings only; scheduled stay on calendar).
        if (meeting.IsActive && !meeting.IsScheduled && !meeting.Participants.Any(p => p.IsPresent))
            meeting.End(now);

        await db.SaveChangesAsync(ct);
        await NotifyAsync(meeting.OrganizationId, ct);
        await PublishMeetingAsync(meeting.Id, ct);
    }

    /// <summary>
    /// Client quit/crash often skips Leave — clear every room this user is still marked present in
    /// so “Happening now” does not keep ghost meetings forever.
    /// </summary>
    public async Task LeavePresentMeetingsAsync(Guid userId, CancellationToken ct = default)
    {
        var meetingIds = await db.MeetingParticipants
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.LeftAt == null)
            .Select(p => p.MeetingId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var meetingId in meetingIds)
        {
            try { await LeaveAsync(userId, meetingId, ct); }
            catch (AppException)
            {
                // Meeting may already have ended or the user left the org.
            }
        }
    }

    public async Task EndAsync(Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        if (meeting.HostId != userId) throw AppException.Forbidden("Only the host can end the meeting for everyone.");
        if (!meeting.IsActive) return;

        meeting.End(time.Now());
        await db.SaveChangesAsync(ct);
        await NotifyAsync(meeting.OrganizationId, ct);
        await PublishMeetingAsync(meeting.Id, ct);
    }

    public async Task AdmitAsync(Guid userId, Guid meetingId, Guid targetUserId, CancellationToken ct = default)
    {
        var meeting = await RequireHostAsync(userId, meetingId, ct);
        var participant = meeting.Participants.FirstOrDefault(p => p.UserId == targetUserId && p.IsPresent && p.InLobby)
                          ?? throw AppException.NotFound("Participant");
        participant.Admit();
        await db.SaveChangesAsync(ct);
        await PublishMeetingAsync(meeting.Id, ct);
    }

    public async Task RejectAsync(Guid userId, Guid meetingId, Guid targetUserId, CancellationToken ct = default)
    {
        var meeting = await RequireHostAsync(userId, meetingId, ct);
        var participant = meeting.Participants.FirstOrDefault(p => p.UserId == targetUserId && p.IsPresent && p.InLobby)
                          ?? throw AppException.NotFound("Participant");
        participant.Reject(time.Now());
        await db.SaveChangesAsync(ct);
        await PublishMeetingAsync(meeting.Id, ct);
        await realtime.ParticipantRemovedAsync(await OrgMemberIdsAsync(meeting.OrganizationId, ct), meeting.Id, targetUserId, ct);
    }

    public async Task RaiseHandAsync(Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        var participant = meeting.Participants.FirstOrDefault(p => p.UserId == userId && p.IsPresent)
                          ?? throw AppException.Validation("Join the meeting before raising a hand.");
        participant.SetHandRaised(true);
        await db.SaveChangesAsync(ct);
        await realtime.HandRaisedAsync(await OrgMemberIdsAsync(meeting.OrganizationId, ct), meeting.Id, userId, true, ct);
        await PublishMeetingAsync(meeting.Id, ct);
    }

    public async Task LowerHandAsync(Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        var participant = meeting.Participants.FirstOrDefault(p => p.UserId == userId && p.IsPresent)
                          ?? throw AppException.Validation("Join the meeting before lowering a hand.");
        participant.SetHandRaised(false);
        await db.SaveChangesAsync(ct);
        await realtime.HandRaisedAsync(await OrgMemberIdsAsync(meeting.OrganizationId, ct), meeting.Id, userId, false, ct);
        await PublishMeetingAsync(meeting.Id, ct);
    }

    public async Task RemoveParticipantAsync(Guid userId, Guid meetingId, Guid targetUserId, CancellationToken ct = default)
    {
        var meeting = await RequireHostAsync(userId, meetingId, ct);
        if (targetUserId == userId) throw AppException.Validation("Leave the meeting instead of removing yourself.");
        var participant = meeting.Participants.FirstOrDefault(p => p.UserId == targetUserId && p.IsPresent)
                          ?? throw AppException.NotFound("Participant");
        participant.Leave(time.Now());
        await db.SaveChangesAsync(ct);
        await realtime.ParticipantRemovedAsync(await OrgMemberIdsAsync(meeting.OrganizationId, ct), meeting.Id, targetUserId, ct);
        await PublishMeetingAsync(meeting.Id, ct);
    }

    public async Task MuteParticipantAsync(Guid userId, Guid meetingId, Guid targetUserId, CancellationToken ct = default)
    {
        var meeting = await RequireHostAsync(userId, meetingId, ct);
        if (!meeting.Participants.Any(p => p.UserId == targetUserId && p.IsPresent && !p.InLobby))
            throw AppException.NotFound("Participant");
        await realtime.ParticipantMutedAsync(await OrgMemberIdsAsync(meeting.OrganizationId, ct), meeting.Id, targetUserId, ct);
    }

    public async Task<MessageDto> SendMeetingChatAsync(
        Guid userId,
        Guid meetingId,
        string? body,
        CancellationToken ct = default,
        IReadOnlyList<Guid>? attachmentIds = null)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        if (!meeting.Participants.Any(p => p.UserId == userId && p.IsPresent && !p.InLobby))
            throw AppException.Validation("Join the meeting before sending chat.");

        return await chat.SendAsync(
            userId, new SendMessageRequest(null, null, body, attachmentIds, null, meetingId), ct);
    }

    public async Task MarkMeetingChatReadAsync(Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        var participant = meeting.Participants.FirstOrDefault(p => p.UserId == userId && p.IsPresent && !p.InLobby)
                          ?? throw AppException.Validation("Join the meeting before reading chat.");
        var now = time.Now();
        participant.MarkRead(now);
        await db.SaveChangesAsync(ct);

        var recipients = meeting.Participants
            .Where(p => p.IsPresent && !p.InLobby)
            .Select(p => p.UserId)
            .Distinct()
            .ToList();
        var receipt = new ReadReceiptDto(null, null, userId, now, meetingId);
        await realtime.ReadReceiptAsync(recipients, receipt, ct);
    }

    public async Task<MeetingRecordingDto> StartRecordingAsync(Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var meeting = await RequireHostAsync(userId, meetingId, ct);
        if (!meeting.IsActive) throw AppException.Conflict("This meeting has ended.");

        var active = await db.MeetingRecordings
            .Where(r => r.MeetingId == meetingId
                        && (r.Status == Domain.RecordingStatus.Starting
                            || r.Status == Domain.RecordingStatus.Recording))
            .FirstOrDefaultAsync(ct);
        if (active is not null)
            throw AppException.Conflict("A recording is already in progress.");

        var recording = Domain.MeetingRecording.Start(meeting.Id, userId, time.Now());
        db.MeetingRecordings.Add(recording);
        await db.SaveChangesAsync(ct);

        return ToRecordingDto(recording);
    }

    public async Task<MeetingRecordingDto> CompleteRecordingAsync(
        Guid userId, Guid meetingId, CompleteRecordingRequest request, CancellationToken ct = default)
    {
        var meeting = await RequireHostAsync(userId, meetingId, ct);
        var recording = await db.MeetingRecordings
            .Where(r => r.MeetingId == meetingId
                        && (r.Status == Domain.RecordingStatus.Starting
                            || r.Status == Domain.RecordingStatus.Recording))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw AppException.Conflict("No recording is in progress.");

        if (recording.StartedById != userId)
            throw AppException.Forbidden("Only the host who started the recording can finish it.");

        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == request.FileId, ct)
                   ?? throw AppException.NotFound("File");
        if (file.MeetingId != meetingId || file.UploaderId != userId)
            throw AppException.Validation("That file is not a recording for this meeting.");

        recording.MarkReady(file.StorageKey, file.FileName, time.Now());
        await db.SaveChangesAsync(ct);
        return ToRecordingDto(recording);
    }

    public async Task<MeetingRecordingDto> CancelRecordingAsync(
        Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        await RequireHostAsync(userId, meetingId, ct);
        var recording = await db.MeetingRecordings
            .Where(r => r.MeetingId == meetingId
                        && (r.Status == Domain.RecordingStatus.Starting
                            || r.Status == Domain.RecordingStatus.Recording))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw AppException.Conflict("No recording is in progress.");

        if (recording.StartedById != userId)
            throw AppException.Forbidden("Only the host who started the recording can stop it.");

        recording.MarkFailed(time.Now());
        await db.SaveChangesAsync(ct);
        return ToRecordingDto(recording);
    }

    private static MeetingRecordingDto ToRecordingDto(Domain.MeetingRecording recording) =>
        new(recording.Id, recording.MeetingId, recording.Status.ToContract(), recording.FileName,
            recording.StartedAt, recording.EndedAt);

    public async Task<MeetingDto> RespondToInviteAsync(
        Guid userId, Guid meetingId, RespondInviteRequest request, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct, includeInvites: true);
        meeting.RespondToInvite(userId, request.Status.ToDomain(), time.Now());
        await db.SaveChangesAsync(ct);
        await NotifyAsync(meeting.OrganizationId, ct);
        await PublishMeetingAsync(meeting.Id, ct);
        return ToDto(await LoadAsync(meeting.Id, ct));
    }

    public async Task<MeetingDto> InviteParticipantsAsync(
        Guid userId, Guid meetingId, InviteParticipantsRequest request, CancellationToken ct = default)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct, includeInvites: true);
        if (!meeting.IsActive) throw AppException.Conflict("This meeting has ended.");
        if (!meeting.Participants.Any(p => p.UserId == userId && p.IsPresent && !p.InLobby))
            throw AppException.Validation("Join the meeting before inviting others.");

        var inviteeIds = (request.UserIds ?? []).Where(id => id != userId).Distinct().ToList();
        if (inviteeIds.Count == 0) throw AppException.Validation("Pick at least one person to invite.");

        var known = await db.Memberships.CountAsync(
            m => m.OrganizationId == meeting.OrganizationId && inviteeIds.Contains(m.UserId), ct);
        if (known != inviteeIds.Count)
            throw AppException.Validation("Everyone invited must belong to the organization.");

        var now = time.Now();
        var added = meeting.InvitePeople(inviteeIds, now);
        if (added.Count == 0) return ToDto(await LoadAsync(meeting.Id, ct));

        var actorName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
        foreach (var invite in added)
        {
            db.ActivityItems.Add(Domain.ActivityItem.Create(
                meeting.OrganizationId, invite.UserId, userId, Domain.ActivityKind.MeetingInvite,
                null, null, null, $"{actorName} invited you to “{meeting.Title}”", now, meeting.Id));
        }

        await db.SaveChangesAsync(ct);
        await NotifyAsync(meeting.OrganizationId, ct);
        await PublishMeetingAsync(meeting.Id, ct);
        await realtime.ActivityChangedAsync(added.Select(i => i.UserId).ToList(), ct);
        return ToDto(await LoadAsync(meeting.Id, ct));
    }

    private async Task<Domain.Meeting> RequireHostAsync(Guid userId, Guid meetingId, CancellationToken ct)
    {
        var meeting = await RequireAccessibleAsync(userId, meetingId, ct);
        if (meeting.HostId != userId) throw AppException.Forbidden("Only the host can do that.");
        return meeting;
    }

    private async Task<Domain.Meeting> RequireAccessibleAsync(
        Guid userId, Guid meetingId, CancellationToken ct, bool includeInvites = false)
    {
        IQueryable<Domain.Meeting> query = db.Meetings.Include(m => m.Participants);
        if (includeInvites) query = query.Include(m => m.Invites);
        var meeting = await query.FirstOrDefaultAsync(m => m.Id == meetingId, ct);
        if (meeting is null) throw AppException.NotFound("Meeting");

        var isMember = await db.Memberships.AnyAsync(
            m => m.OrganizationId == meeting.OrganizationId && m.UserId == userId, ct);
        return isMember ? meeting : throw AppException.NotFound("Meeting");
    }

    private async Task<Domain.Meeting> LoadAsync(Guid meetingId, CancellationToken ct) =>
        await db.Meetings.AsNoTracking()
            .Include(m => m.Host)
            .Include(m => m.Participants).ThenInclude(p => p.User)
            .Include(m => m.Invites).ThenInclude(i => i.User)
            .FirstAsync(m => m.Id == meetingId, ct);

    private async Task NotifyAsync(Guid organizationId, CancellationToken ct) =>
        await realtime.MeetingsChangedAsync(await OrgMemberIdsAsync(organizationId, ct), ct);

    private Task<List<Guid>> OrgMemberIdsAsync(Guid organizationId, CancellationToken ct) =>
        db.Memberships.Where(m => m.OrganizationId == organizationId).Select(m => m.UserId).ToListAsync(ct);

    private async Task PublishMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        var meeting = await LoadAsync(meetingId, ct);
        await realtime.MeetingUpdatedAsync(await OrgMemberIdsAsync(meeting.OrganizationId, ct), ToDto(meeting), ct);
    }

    private static MeetingDto ToDto(Domain.Meeting meeting) =>
        new(meeting.Id,
            meeting.Title,
            meeting.Description,
            meeting.Host.ToSummary(),
            meeting.CreatedAt,
            meeting.StartsAt,
            meeting.EndsAt,
            meeting.IsActive,
            meeting.LobbyEnabled,
            meeting.IsScheduled,
            meeting.Recurrence.ToContract(),
            meeting.RecurrenceUntil,
            meeting.Participants
                .Where(p => p.IsPresent && !p.InLobby)
                .OrderBy(p => p.JoinedAt)
                .Select(p => p.User.ToSummary())
                .DistinctBy(u => u.Id)
                .ToList(),
            meeting.Invites
                .OrderBy(i => i.User.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(i => new MeetingInviteDto(i.UserId, i.User.DisplayName, i.Status.ToContract()))
                .ToList(),
            meeting.Participants
                .Where(p => p.IsPresent && p.InLobby)
                .OrderBy(p => p.JoinedAt)
                .Select(p => p.User.ToSummary())
                .DistinctBy(u => u.Id)
                .ToList(),
            meeting.Participants
                .Where(p => p.IsPresent && p.HandRaised)
                .Select(p => p.UserId)
                .ToList(),
            meeting.Participants
                .Where(p => p.IsPresent && !p.InLobby)
                .GroupBy(p => p.UserId)
                .Select(g => g.OrderByDescending(p => p.JoinedAt).First())
                .OrderBy(p => p.User.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(p => new ReadCursorDto(p.UserId, p.User.DisplayName, p.LastReadAt))
                .ToList());

    private static List<string> NormalizeEmails(IReadOnlyList<string>? emails) =>
        (emails ?? [])
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0 && e.Contains('@') && !e.Contains(' '))
            .ToList();

    private async Task<(Domain.Meeting Meeting, string? InviteEmail, string? SuggestedName, string Token)>
        ResolveInviteTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw AppException.NotFound("Invite");

        var guest = await db.MeetingGuestInvites
            .Include(i => i.Meeting).ThenInclude(m => m.Host)
            .Include(i => i.Meeting).ThenInclude(m => m.Invites)
            .Include(i => i.Meeting).ThenInclude(m => m.GuestInvites)
            .Include(i => i.Meeting).ThenInclude(m => m.Participants)
            .FirstOrDefaultAsync(i => i.InviteToken == token, ct);
        if (guest is not null)
            return (guest.Meeting, guest.Email, guest.DisplayName, guest.InviteToken);

        var member = await db.MeetingInvites
            .Include(i => i.User)
            .Include(i => i.Meeting).ThenInclude(m => m.Host)
            .Include(i => i.Meeting).ThenInclude(m => m.Invites)
            .Include(i => i.Meeting).ThenInclude(m => m.GuestInvites)
            .Include(i => i.Meeting).ThenInclude(m => m.Participants)
            .FirstOrDefaultAsync(i => i.InviteToken == token, ct)
            ?? throw AppException.NotFound("Invite");

        return (member.Meeting, member.User.Email, member.User.DisplayName, member.InviteToken);
    }

    private async Task SendScheduleInviteEmailsAsync(Guid meetingId, string hostName, CancellationToken ct)
    {
        var meeting = await db.Meetings
            .AsNoTracking()
            .Include(m => m.Host)
            .Include(m => m.Invites).ThenInclude(i => i.User)
            .Include(m => m.GuestInvites)
            .FirstAsync(m => m.Id == meetingId, ct);

        foreach (var invite in meeting.Invites)
        {
            if (invite.User.Email is null) continue;
            var (subject, html) = MeetingInviteMail.Build(
                meeting.Title, hostName, meeting.StartsAt, meeting.EndsAt, meeting.Description,
                _urls.JoinUrl(invite.InviteToken), _urls.DesktopDeepLink(invite.InviteToken));
            await email.SendAsync(invite.User.Email, subject, html, ct);
        }

        foreach (var invite in meeting.GuestInvites)
        {
            var (subject, html) = MeetingInviteMail.Build(
                meeting.Title, hostName, meeting.StartsAt, meeting.EndsAt, meeting.Description,
                _urls.JoinUrl(invite.InviteToken), _urls.DesktopDeepLink(invite.InviteToken));
            await email.SendAsync(invite.Email, subject, html, ct);
        }
    }
}
