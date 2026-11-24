using System.Text.RegularExpressions;
using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Chat;

/// <summary>1:1 and group conversations, plus messages in both conversations and channels.</summary>
public sealed partial class ChatService(IAppDbContext db, IRealtimePublisher realtime, TimeProvider time)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(
        Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);
        var conversations = await LoadConversationsAsync(
            userId, db.ConversationMembers.Where(m => m.UserId == userId && m.Conversation.OrganizationId == organizationId), ct);
        return conversations;
    }

    public async Task<ConversationDto> CreateConversationAsync(
        Guid userId, Guid organizationId, CreateConversationRequest request, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var others = (request.MemberIds ?? []).Where(id => id != userId).Distinct().ToList();
        if (others.Count == 0) throw AppException.Validation("Choose at least one other person.");

        var knownMembers = await db.Memberships.CountAsync(
            m => m.OrganizationId == organizationId && others.Contains(m.UserId), ct);
        if (knownMembers != others.Count)
            throw AppException.Validation("Everyone in a conversation must belong to the organization.");

        var now = time.Now();
        Domain.Conversation conversation;
        if (others.Count == 1)
        {
            var existing = await FindDirectAsync(organizationId, userId, others[0], ct);
            if (existing is not null) return await GetConversationAsync(userId, existing.Value, ct);
            conversation = Domain.Conversation.CreateDirect(organizationId, userId, others[0], now);
        }
        else
        {
            conversation = Domain.Conversation.CreateGroup(organizationId, userId, others, request.Title, now);
        }

        db.Conversations.Add(conversation);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (conversation.Kind == Domain.ConversationKind.Direct)
        {
            // Both people started the same direct chat at once; the unique key let one through.
            db.Conversations.Remove(conversation);
            var winner = await FindDirectAsync(organizationId, userId, others[0], ct);
            if (winner is null) throw;
            return await GetConversationAsync(userId, winner.Value, ct);
        }

        await realtime.ConversationsChangedAsync(conversation.Members.Select(m => m.UserId).ToList(), ct);
        return await GetConversationAsync(userId, conversation.Id, ct);
    }

    public async Task<ConversationDto> UpdateConversationPrefsAsync(
        Guid userId, Guid conversationId, UpdateConversationPrefsRequest request, CancellationToken ct = default)
    {
        var member = await db.ConversationMembers.FirstOrDefaultAsync(
                         m => m.ConversationId == conversationId && m.UserId == userId, ct)
                     ?? throw AppException.NotFound("Conversation");
        member.SetPreferences(request.IsMuted, request.IsPinned, request.NotifyMode.ToDomain(), time.Now());
        await db.SaveChangesAsync(ct);
        return await GetConversationAsync(userId, conversationId, ct);
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        Guid userId, Guid? conversationId, Guid? channelId, DateTimeOffset? before, int? limit, CancellationToken ct = default)
    {
        await db.ResolveTargetAsync(userId, conversationId, channelId, ct);

        // Main history is root messages only; replies are loaded via GetRepliesAsync.
        var query = conversationId is { } cid
            ? db.Messages.AsNoTracking().Where(m => m.ConversationId == cid && m.ParentMessageId == null)
            : db.Messages.AsNoTracking().Where(m => m.ChannelId == channelId && m.ParentMessageId == null);
        if (before is { } cutoff)
        {
            var utc = cutoff.ToUniversalTime();
            query = query.Where(m => m.CreatedAt < utc);
        }

        var page = await ProjectMessagesAsync(
            query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Take(Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize)),
            userId, ct);

        page.Reverse(); // Oldest first, the order a thread is read in.
        return page;
    }

    public async Task<IReadOnlyList<MessageDto>> GetMeetingMessagesAsync(
        Guid userId, Guid meetingId, DateTimeOffset? before, int? limit, CancellationToken ct = default)
    {
        await db.ResolveTargetAsync(userId, null, null, ct, meetingId);

        var query = db.Messages.AsNoTracking().Where(m => m.MeetingId == meetingId && m.ParentMessageId == null);
        if (before is { } cutoff)
        {
            var utc = cutoff.ToUniversalTime();
            query = query.Where(m => m.CreatedAt < utc);
        }

        var page = await ProjectMessagesAsync(
            query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Take(Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize)),
            userId, ct);
        page.Reverse();
        return page;
    }

    public async Task<IReadOnlyList<MessageDto>> GetRepliesAsync(
        Guid userId, Guid parentMessageId, CancellationToken ct = default)
    {
        var parent = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == parentMessageId, ct)
                     ?? throw AppException.NotFound("Message");
        await db.ResolveTargetAsync(userId, parent.ConversationId, parent.ChannelId, ct, parent.MeetingId);

        var page = await ProjectMessagesAsync(
            db.Messages.AsNoTracking()
                .Where(m => m.ParentMessageId == parentMessageId)
                .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id),
            userId, ct);
        return page;
    }

    public async Task<MessageDto> SendAsync(Guid userId, SendMessageRequest request, CancellationToken ct = default)
    {
        var target = await db.ResolveTargetAsync(
            userId, request.ConversationId, request.ChannelId, ct, request.MeetingId);
        var now = time.Now();

        Domain.Message? parent = null;
        if (request.ParentMessageId is { } parentId)
        {
            parent = await db.Messages.FirstOrDefaultAsync(m => m.Id == parentId, ct)
                     ?? throw AppException.NotFound("Message");
            if (parent.IsDeleted) throw AppException.Validation("That message was deleted.");
            if (parent.ParentMessageId is not null)
                throw AppException.Validation("Replies can only be added to a top-level message.");
            if (parent.ConversationId != request.ConversationId
                || parent.ChannelId != request.ChannelId
                || parent.MeetingId != request.MeetingId)
                throw AppException.Validation("A reply must stay in the same conversation, channel, or meeting.");
        }

        var fileIds = (request.AttachmentIds ?? []).Distinct().ToList();
        var files = new List<Domain.FileObject>();
        if (fileIds.Count > 0)
        {
            // Only the uploader's own, not-yet-sent files that were uploaded to this same place.
            files = await db.Files
                .Where(f => fileIds.Contains(f.Id)
                            && f.UploaderId == userId
                            && f.ConversationId == request.ConversationId
                            && f.ChannelId == request.ChannelId
                            && f.MeetingId == request.MeetingId
                            && !db.Attachments.Any(a => a.FileObjectId == f.Id))
                .ToListAsync(ct);
            if (files.Count != fileIds.Count)
                throw AppException.Validation("One or more attachments are no longer available.");
        }

        var message = Domain.Message.Create(
            target.OrganizationId, userId, request.ConversationId, request.ChannelId, request.Body, fileIds, now,
            request.ParentMessageId, request.MeetingId);
        db.Messages.Add(message);

        if (parent is not null) parent.AddReply();

        if (target.Conversation is { } conversation && parent is null)
        {
            conversation.RecordMessage(message);
            conversation.Members.First(m => m.UserId == userId).MarkRead(now);
        }

        var activityRecipients = await BuildActivityAsync(userId, message, parent, target, now, ct);
        await db.SaveChangesAsync(ct);

        var dto = await GetMessageDtoAsync(message.Id, userId, ct);
        if (request.MeetingId is not null)
            await realtime.MeetingChatAsync(target.MemberIds, dto, ct);
        else
            await realtime.MessageCreatedAsync(target.MemberIds, dto, ct);
        if (activityRecipients.Count > 0)
            await realtime.ActivityChangedAsync(activityRecipients, ct);
        if (target.Conversation is not null && parent is null)
            await realtime.ConversationsChangedAsync(target.MemberIds, ct);
        return dto;
    }

    public async Task<MessageDto> EditAsync(
        Guid userId, Guid messageId, EditMessageRequest request, CancellationToken ct = default)
    {
        var message = await db.Messages.Include(m => m.Attachments)
                          .FirstOrDefaultAsync(m => m.Id == messageId, ct)
                      ?? throw AppException.NotFound("Message");
        if (message.SenderId != userId) throw AppException.Forbidden("Only the author can edit this message.");

        var target = await db.ResolveTargetAsync(userId, message.ConversationId, message.ChannelId, ct, message.MeetingId);
        var now = time.Now();
        message.Edit(request.Body, now);

        if (target.Conversation is { } conversation
            && message.ParentMessageId is null
            && conversation.LastMessageAt == message.CreatedAt)
        {
            conversation.RecordMessage(message);
        }

        await db.SaveChangesAsync(ct);

        var dto = await GetMessageDtoAsync(message.Id, userId, ct);
        await realtime.MessageUpdatedAsync(target.MemberIds, dto, ct);
        return dto;
    }

    public async Task<MessageDto> DeleteAsync(Guid userId, Guid messageId, CancellationToken ct = default)
    {
        var message = await db.Messages.Include(m => m.Attachments)
                          .FirstOrDefaultAsync(m => m.Id == messageId, ct)
                      ?? throw AppException.NotFound("Message");
        if (message.SenderId != userId) throw AppException.Forbidden("Only the author can delete this message.");

        var target = await db.ResolveTargetAsync(userId, message.ConversationId, message.ChannelId, ct, message.MeetingId);
        var now = time.Now();
        message.SoftDelete(now);

        if (message.ParentMessageId is { } parentId)
        {
            var parent = await db.Messages.FirstOrDefaultAsync(m => m.Id == parentId, ct);
            parent?.RemoveReply();
        }

        if (target.Conversation is { } conversation
            && message.ParentMessageId is null
            && conversation.LastMessageAt == message.CreatedAt)
        {
            conversation.RecordMessage(message);
        }

        await db.SaveChangesAsync(ct);

        var dto = await GetMessageDtoAsync(message.Id, userId, ct);
        await realtime.MessageUpdatedAsync(target.MemberIds, dto, ct);
        return dto;
    }

    public async Task<MessageDto> ToggleReactionAsync(
        Guid userId, Guid messageId, ToggleReactionRequest request, CancellationToken ct = default)
    {
        var message = await db.Messages.Include(m => m.Reactions)
                          .FirstOrDefaultAsync(m => m.Id == messageId, ct)
                      ?? throw AppException.NotFound("Message");
        if (message.IsDeleted) throw AppException.Validation("That message was deleted.");

        var target = await db.ResolveTargetAsync(userId, message.ConversationId, message.ChannelId, ct, message.MeetingId);
        var now = time.Now();
        var emoji = request.Emoji?.Trim() ?? string.Empty;
        var existing = message.Reactions.FirstOrDefault(r => r.UserId == userId && r.Emoji == emoji);

        List<Guid> activityRecipients = [];
        if (existing is not null)
        {
            db.MessageReactions.Remove(existing);
        }
        else
        {
            var reaction = Domain.MessageReaction.Create(message.Id, userId, emoji, now);
            db.MessageReactions.Add(reaction);

            if (message.SenderId != userId)
            {
                var actorName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
                var activity = Domain.ActivityItem.Create(
                    target.OrganizationId, message.SenderId, userId, Domain.ActivityKind.Reaction,
                    message.Id, message.ConversationId, message.ChannelId,
                    $"{actorName} reacted with {reaction.Emoji}", now, message.MeetingId);
                db.ActivityItems.Add(activity);
                activityRecipients.Add(message.SenderId);
            }
        }

        await db.SaveChangesAsync(ct);

        var dto = await GetMessageDtoAsync(message.Id, userId, ct);
        await realtime.MessageUpdatedAsync(target.MemberIds, dto, ct);
        if (activityRecipients.Count > 0)
            await realtime.ActivityChangedAsync(activityRecipients, ct);
        return dto;
    }

    public async Task MarkReadAsync(Guid userId, Guid conversationId, CancellationToken ct = default)
    {
        var member = await db.ConversationMembers
                         .Include(m => m.Conversation).ThenInclude(c => c.Members)
                         .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == userId, ct)
                     ?? throw AppException.NotFound("Conversation");
        var now = time.Now();
        member.MarkRead(now);
        await db.SaveChangesAsync(ct);

        var receipt = new ReadReceiptDto(conversationId, null, userId, now);
        await realtime.ReadReceiptAsync(member.Conversation.Members.Select(m => m.UserId).ToList(), receipt, ct);
    }

    public async Task MarkChannelReadAsync(Guid userId, Guid channelId, CancellationToken ct = default)
    {
        var target = await db.ResolveTargetAsync(userId, null, channelId, ct);
        var now = time.Now();
        var member = await db.ChannelMembers.FirstOrDefaultAsync(
            m => m.ChannelId == channelId && m.UserId == userId, ct);
        if (member is not null)
        {
            member.MarkRead(now);
            await db.SaveChangesAsync(ct);
        }

        var receipt = new ReadReceiptDto(null, channelId, userId, now);
        await realtime.ReadReceiptAsync(target.MemberIds, receipt, ct);
    }

    public async Task SetTypingAsync(
        Guid userId, Guid? conversationId, Guid? channelId, bool isTyping, CancellationToken ct = default)
    {
        var target = await db.ResolveTargetAsync(userId, conversationId, channelId, ct);
        var displayName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
        var dto = new TypingDto(conversationId, channelId, userId, displayName, isTyping);
        var others = target.MemberIds.Where(id => id != userId).ToList();
        if (others.Count > 0) await realtime.TypingAsync(others, dto, ct);
    }

    private async Task<List<Guid>> BuildActivityAsync(
        Guid userId,
        Domain.Message message,
        Domain.Message? parent,
        MessageTarget target,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var recipients = new HashSet<Guid>();
        var actorName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);

        if (parent is not null && parent.SenderId != userId)
        {
            db.ActivityItems.Add(Domain.ActivityItem.Create(
                target.OrganizationId, parent.SenderId, userId, Domain.ActivityKind.Reply,
                message.Id, message.ConversationId, message.ChannelId,
                $"{actorName} replied: {message.Preview()}", now, message.MeetingId));
            recipients.Add(parent.SenderId);
        }

        if (message.Body.Length > 0)
        {
            var mentionNames = MentionPattern().Matches(message.Body)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (mentionNames.Count > 0)
            {
                var people = await db.Users
                    .Where(u => target.MemberIds.Contains(u.Id) && u.Id != userId)
                    .Select(u => new { u.Id, u.DisplayName })
                    .ToListAsync(ct);
                foreach (var person in people.Where(p =>
                             mentionNames.Any(n => string.Equals(n, p.DisplayName, StringComparison.OrdinalIgnoreCase))))
                {
                    if (!recipients.Add(person.Id)) continue;
                    db.ActivityItems.Add(Domain.ActivityItem.Create(
                        target.OrganizationId, person.Id, userId, Domain.ActivityKind.Mention,
                        message.Id, message.ConversationId, message.ChannelId,
                        $"{actorName} mentioned you: {message.Preview()}", now, message.MeetingId));
                }
            }
        }

        return recipients.ToList();
    }

    private async Task<MessageDto> GetMessageDtoAsync(Guid messageId, Guid viewerId, CancellationToken ct)
    {
        var list = await ProjectMessagesAsync(
            db.Messages.AsNoTracking().Where(m => m.Id == messageId), viewerId, ct);
        return list.FirstOrDefault() ?? throw AppException.NotFound("Message");
    }

    private static async Task<List<MessageDto>> ProjectMessagesAsync(
        IQueryable<Domain.Message> query, Guid viewerId, CancellationToken ct)
    {
        return await query
            .Select(m => new MessageDto(
                m.Id,
                m.ConversationId,
                m.ChannelId,
                new UserSummaryDto(
                    m.SenderId,
                    m.Sender.DisplayName,
                    m.Sender.AvatarStorageKey != null
                        ? "api/users/" + m.SenderId.ToString() + "/avatar"
                        : null),
                m.DeletedAt == null ? m.Body : string.Empty,
                m.CreatedAt,
                m.Attachments
                    .Select(a => new AttachmentDto(
                        a.FileObjectId, a.FileObject.FileName, a.FileObject.ContentType, a.FileObject.Size))
                    .ToList(),
                m.ParentMessageId,
                m.EditedAt,
                m.DeletedAt != null,
                m.ReplyCount,
                m.Reactions
                    .GroupBy(r => r.Emoji)
                    .OrderBy(g => g.Key)
                    .Select(g => new ReactionSummaryDto(g.Key, g.Count(), g.Any(r => r.UserId == viewerId)))
                    .ToList(),
                m.MeetingId,
                m.LinkPreviewUrl == null
                    ? null
                    : new LinkPreviewDto(m.LinkPreviewUrl, m.LinkPreviewTitle, m.LinkPreviewDescription)))
            .ToListAsync(ct);
    }

    private async Task<Guid?> FindDirectAsync(Guid organizationId, Guid userA, Guid userB, CancellationToken ct)
    {
        var key = Domain.Conversation.DirectKeyFor(userA, userB);
        var id = await db.Conversations
            .Where(c => c.OrganizationId == organizationId && c.DirectKey == key)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        return id;
    }

    private async Task<ConversationDto> GetConversationAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conversations = await LoadConversationsAsync(
            userId, db.ConversationMembers.Where(m => m.UserId == userId && m.ConversationId == conversationId), ct);
        return conversations.FirstOrDefault() ?? throw AppException.NotFound("Conversation");
    }

    private async Task<List<ConversationDto>> LoadConversationsAsync(
        Guid userId, IQueryable<Domain.ConversationMember> mine, CancellationToken ct)
    {
        var rows = await mine
            .Select(m => new
            {
                m.Conversation.Id,
                m.Conversation.Kind,
                m.Conversation.Title,
                m.Conversation.LastMessagePreview,
                m.Conversation.LastMessageAt,
                m.Conversation.CreatedAt,
                m.IsMuted,
                m.IsPinned,
                m.NotifyMode,
                m.PinnedAt,
                Unread = db.Messages.Count(x =>
                    x.ConversationId == m.ConversationId
                    && x.ParentMessageId == null
                    && x.DeletedAt == null
                    && x.SenderId != userId
                    && x.CreatedAt > m.LastReadAt),
            })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var members = await db.ConversationMembers
            .Where(m => ids.Contains(m.ConversationId))
            .Select(m => new { m.ConversationId, m.UserId, m.User.DisplayName, m.User.AvatarStorageKey })
            .ToListAsync(ct);
        var membersByConversation = members.ToLookup(m => m.ConversationId);

        return rows
            .OrderByDescending(r => r.IsPinned)
            .ThenByDescending(r => r.PinnedAt)
            .ThenByDescending(r => r.LastMessageAt ?? r.CreatedAt)
            .Select(r => new ConversationDto(
                r.Id,
                r.Kind.ToContract(),
                r.Title,
                membersByConversation[r.Id]
                    .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new UserSummaryDto(m.UserId, m.DisplayName, Mapping.AvatarUrl(m.UserId, m.AvatarStorageKey)))
                    .ToList(),
                r.LastMessagePreview,
                r.LastMessageAt,
                r.Unread,
                r.IsMuted,
                r.IsPinned,
                r.NotifyMode.ToContract()))
            .ToList();
    }

    [GeneratedRegex(@"@([^\s@][^@]*?)(?=\s|$|[,.!?])", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();
}
