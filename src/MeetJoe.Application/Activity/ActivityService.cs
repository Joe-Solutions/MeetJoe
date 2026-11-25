using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MeetJoe.Application.Activity;

/// <summary>Per-user feed of replies, reactions, and mentions.</summary>
public sealed class ActivityService(IAppDbContext db, TimeProvider time)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<IReadOnlyList<ActivityDto>> GetAsync(
        Guid userId, Guid organizationId, int? limit, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var rows = await db.ActivityItems.AsNoTracking()
            .Where(a => a.RecipientId == userId && a.OrganizationId == organizationId)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Take(Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize))
            .Select(a => new
            {
                a.Id,
                a.Kind,
                a.ActorId,
                ActorName = a.Actor.DisplayName,
                ActorAvatarKey = a.Actor.AvatarStorageKey,
                ActorAvatarUpdatedAt = a.Actor.AvatarUpdatedAt,
                a.Summary,
                a.MessageId,
                a.ConversationId,
                a.ChannelId,
                a.CreatedAt,
                IsUnread = a.ReadAt == null,
                a.MeetingId,
                a.CallId,
            })
            .ToListAsync(ct);

        return rows.Select(a => new ActivityDto(
                a.Id,
                a.Kind.ToContract(),
                new UserSummaryDto(
                    a.ActorId,
                    a.ActorName,
                    Mapping.AvatarUrl(a.ActorId, a.ActorAvatarKey, a.ActorAvatarUpdatedAt)),
                a.Summary,
                a.MessageId,
                a.ConversationId,
                a.ChannelId,
                a.CreatedAt,
                a.IsUnread,
                a.MeetingId,
                a.CallId))
            .ToList();
    }

    public async Task MarkAllReadAsync(Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);
        var now = time.Now();
        var items = await db.ActivityItems
            .Where(a => a.RecipientId == userId && a.OrganizationId == organizationId && a.ReadAt == null)
            .ToListAsync(ct);
        foreach (var item in items) item.MarkRead(now);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkReadAsync(Guid userId, Guid activityId, CancellationToken ct = default)
    {
        var item = await db.ActivityItems.FirstOrDefaultAsync(a => a.Id == activityId && a.RecipientId == userId, ct)
                   ?? throw AppException.NotFound("Activity");
        item.MarkRead(time.Now());
        await db.SaveChangesAsync(ct);
    }
}
