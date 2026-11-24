using MeetJoe.Application.Common;
using MeetJoe.Application.Meetings;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Presence;

/// <summary>
/// A user's visible status is Offline while they have no live connection, and otherwise the
/// availability they chose.
/// </summary>
public sealed class PresenceService(
    IAppDbContext db,
    IPresenceStore store,
    IRealtimePublisher realtime,
    MeetingService meetings)
{
    public async Task ConnectedAsync(Guid userId, string connectionId, CancellationToken ct = default)
    {
        if (await store.AddConnectionAsync(userId, connectionId)) await BroadcastAsync(userId, ct);
    }

    public async Task DisconnectedAsync(Guid userId, string connectionId, CancellationToken ct = default)
    {
        if (!await store.RemoveConnectionAsync(userId, connectionId)) return;

        // Last connection dropped (quit, crash, or network). Free ghost meeting seats.
        await meetings.LeavePresentMeetingsAsync(userId, ct);
        await BroadcastAsync(userId, ct);
    }

    public async Task<PresenceDto> SetAvailabilityAsync(Guid userId, PresenceStatus status, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        user.SetAvailability(status.ToAvailability());
        await db.SaveChangesAsync(ct);
        return await BroadcastAsync(userId, ct);
    }

    public async Task<PresenceDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw AppException.NotFound("User");
        var statuses = await ResolveAsync([(user.Id, user.Availability)]);
        return new PresenceDto(user.Id, statuses[user.Id], user.StatusMessage);
    }

    /// <summary>Combines stored availability with live connection state.</summary>
    public async Task<IReadOnlyDictionary<Guid, PresenceStatus>> ResolveAsync(
        IReadOnlyCollection<(Guid UserId, Domain.Availability Availability)> users)
    {
        var online = await store.GetOnlineAsync(users.Select(u => u.UserId).ToList());
        var result = new Dictionary<Guid, PresenceStatus>(users.Count);
        foreach (var (userId, availability) in users)
            result[userId] = online.Contains(userId) ? availability.ToStatus() : PresenceStatus.Offline;
        return result;
    }

    /// <summary>Tells everyone who can see this user what their status is now.</summary>
    public async Task<PresenceDto> BroadcastAsync(Guid userId, CancellationToken ct = default)
    {
        var presence = await GetAsync(userId, ct);
        var audience = await db.GetColleagueIdsAsync(userId, ct);
        await realtime.PresenceChangedAsync(audience, presence, ct);
        return presence;
    }
}
