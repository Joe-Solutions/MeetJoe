using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Search;

public sealed class SearchService(IAppDbContext db)
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 50;

    public async Task<SearchResultDto> SearchAsync(
        Guid userId, Guid organizationId, string? query, int? limit, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var term = (query ?? string.Empty).Trim();
        if (term.Length == 0)
            return new SearchResultDto([], []);

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var needle = term.ToLowerInvariant();

        var peopleRows = await db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId)
            .Where(m => m.User.DisplayName.ToLower().Contains(needle)
                        || (m.User.Email != null && m.User.Email.ToLower().Contains(needle)))
            .OrderBy(m => m.User.DisplayName)
            .Take(take)
            .Select(m => new
            {
                m.UserId,
                m.User.DisplayName,
                m.User.AvatarStorageKey,
                m.User.AvatarUpdatedAt,
            })
            .ToListAsync(ct);
        var people = peopleRows
            .Select(m => new UserSummaryDto(
                m.UserId,
                m.DisplayName,
                Mapping.AvatarUrl(m.UserId, m.AvatarStorageKey, m.AvatarUpdatedAt)))
            .ToList();

        var conversationIds = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Conversation.OrganizationId == organizationId)
            .Select(m => m.ConversationId)
            .ToListAsync(ct);

        var channelRows = await db.Channels.AsNoTracking()
            .Where(c => c.Workspace.OrganizationId == organizationId
                        && c.Workspace.Members.Any(m => m.UserId == userId)
                        && (c.Kind != Domain.ChannelKind.Private || c.Members.Any(m => m.UserId == userId)))
            .Select(c => new { c.Id, c.Name, WorkspaceName = c.Workspace.Name })
            .ToListAsync(ct);
        var channelIds = channelRows.Select(c => c.Id).ToList();
        var channelMeta = channelRows.ToDictionary(c => c.Id);

        var hits = await db.Messages.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId
                        && m.DeletedAt == null
                        && m.ParentMessageId == null
                        && m.MeetingId == null
                        && ((m.ConversationId != null && conversationIds.Contains(m.ConversationId.Value))
                            || (m.ChannelId != null && channelIds.Contains(m.ChannelId.Value)))
                        && m.Body.ToLower().Contains(needle))
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new
            {
                m.Id,
                m.ConversationId,
                m.ChannelId,
                m.Body,
                m.CreatedAt,
                m.SenderId,
                SenderName = m.Sender.DisplayName,
                SenderAvatarKey = m.Sender.AvatarStorageKey,
                SenderAvatarUpdatedAt = m.Sender.AvatarUpdatedAt,
            })
            .ToListAsync(ct);

        var conversationTitles = await db.Conversations.AsNoTracking()
            .Where(c => conversationIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Kind, c.Title })
            .ToListAsync(ct);
        var titleById = conversationTitles.ToDictionary(c => c.Id);

        var messages = hits.Select(h =>
        {
            string location;
            if (h.ChannelId is { } chid && channelMeta.TryGetValue(chid, out var channel))
                location = $"{channel.WorkspaceName} / {channel.Name}";
            else if (h.ConversationId is { } cid && titleById.TryGetValue(cid, out var conv))
                location = conv.Kind == Domain.ConversationKind.Direct
                    ? "Direct chat"
                    : (conv.Title ?? "Group chat");
            else
                location = "Chat";

            var preview = h.Body.Length <= 140 ? h.Body : h.Body[..139] + "…";
            var sender = new UserSummaryDto(
                h.SenderId,
                h.SenderName,
                Mapping.AvatarUrl(h.SenderId, h.SenderAvatarKey, h.SenderAvatarUpdatedAt));
            return new MessageSearchHitDto(
                h.Id, h.ConversationId, h.ChannelId, preview, sender, h.CreatedAt, location);
        }).ToList();

        return new SearchResultDto(people, messages);
    }
}
