namespace MeetJoe.Domain;

public class Conversation
{
    public const int MaxTitleLength = 80;
    public const int MaxGroupMembers = 250;

    private Conversation() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public ConversationKind Kind { get; private set; }
    public string? Title { get; private set; }

    /// <summary>Set for direct conversations only; unique per organization so a pair has one thread.</summary>
    public string? DirectKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastMessageAt { get; private set; }

    /// <summary>Denormalized so the conversation list does not have to read the message table.</summary>
    public string? LastMessagePreview { get; private set; }

    public List<ConversationMember> Members { get; private set; } = [];
    public List<Message> Messages { get; private set; } = [];

    public static Conversation CreateDirect(Guid organizationId, Guid userA, Guid userB, DateTimeOffset now)
    {
        if (userA == userB) throw new DomainException("A direct conversation needs two different people.");

        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Kind = ConversationKind.Direct,
            DirectKey = DirectKeyFor(userA, userB),
            CreatedAt = now,
        };
        conversation.Members.Add(new ConversationMember(conversation.Id, userA, now));
        conversation.Members.Add(new ConversationMember(conversation.Id, userB, now));
        return conversation;
    }

    public static Conversation CreateGroup(
        Guid organizationId, Guid creatorId, IEnumerable<Guid> otherMemberIds, string? title, DateTimeOffset now)
    {
        var others = otherMemberIds.Where(id => id != creatorId).Distinct().ToList();
        if (others.Count < 2) throw new DomainException("A group conversation needs at least three people.");
        if (others.Count + 1 > MaxGroupMembers)
            throw new DomainException($"A group conversation can have at most {MaxGroupMembers} people.");

        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Kind = ConversationKind.Group,
            Title = Guard.Optional(title, "Title", MaxTitleLength),
            CreatedAt = now,
        };
        conversation.Members.Add(new ConversationMember(conversation.Id, creatorId, now));
        foreach (var id in others) conversation.Members.Add(new ConversationMember(conversation.Id, id, now));
        return conversation;
    }

    /// <summary>Order-independent key for a pair of users.</summary>
    public static string DirectKeyFor(Guid userA, Guid userB) =>
        userA.CompareTo(userB) < 0 ? $"{userA:N}:{userB:N}" : $"{userB:N}:{userA:N}";

    public void RecordMessage(Message message)
    {
        LastMessageAt = message.CreatedAt;
        LastMessagePreview = message.Preview();
    }
}

public class ConversationMember(Guid conversationId, Guid userId, DateTimeOffset joinedAt)
{
    public Guid ConversationId { get; private set; } = conversationId;
    public Guid UserId { get; private set; } = userId;
    public DateTimeOffset JoinedAt { get; private set; } = joinedAt;
    public DateTimeOffset LastReadAt { get; private set; } = joinedAt;
    public bool IsMuted { get; private set; }
    public bool IsPinned { get; private set; }
    public DateTimeOffset? PinnedAt { get; private set; }
    public NotifyMode NotifyMode { get; private set; } = NotifyMode.All;

    public Conversation Conversation { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public void MarkRead(DateTimeOffset at)
    {
        if (at > LastReadAt) LastReadAt = at;
    }

    public void SetPreferences(bool isMuted, bool isPinned, NotifyMode notifyMode, DateTimeOffset now)
    {
        IsMuted = isMuted;
        NotifyMode = notifyMode;
        if (isPinned && !IsPinned) PinnedAt = now;
        if (!isPinned) PinnedAt = null;
        IsPinned = isPinned;
    }
}

public class Message
{
    public const int MaxBodyLength = 8000;
    public const int MaxAttachments = 10;
    public const int MaxPreviewLength = 140;

    private Message() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public Guid? ChannelId { get; private set; }
    public Guid? MeetingId { get; private set; }

    /// <summary>When set, this message is a reply in a thread rooted at the parent.</summary>
    public Guid? ParentMessageId { get; private set; }

    public Guid SenderId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? EditedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Number of direct replies. Kept in sync when replies are added or soft-deleted.</summary>
    public int ReplyCount { get; private set; }

    public string? LinkPreviewUrl { get; private set; }
    public string? LinkPreviewTitle { get; private set; }
    public string? LinkPreviewDescription { get; private set; }

    public User Sender { get; private set; } = null!;
    public Message? Parent { get; private set; }
    public List<Attachment> Attachments { get; private set; } = [];
    public List<MessageReaction> Reactions { get; private set; } = [];

    public bool IsDeleted => DeletedAt is not null;

    public static Message Create(
        Guid organizationId,
        Guid senderId,
        Guid? conversationId,
        Guid? channelId,
        string? body,
        IReadOnlyCollection<Guid> attachmentFileIds,
        DateTimeOffset now,
        Guid? parentMessageId = null,
        Guid? meetingId = null)
    {
        var targets = (conversationId is not null ? 1 : 0)
                      + (channelId is not null ? 1 : 0)
                      + (meetingId is not null ? 1 : 0);
        if (targets != 1)
            throw new DomainException("A message belongs to exactly one conversation, channel, or meeting.");

        var text = NormalizeBody(body, requireContent: attachmentFileIds.Count == 0);
        if (attachmentFileIds.Count > MaxAttachments)
            throw new DomainException($"A message can carry at most {MaxAttachments} attachments.");

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            SenderId = senderId,
            ConversationId = conversationId,
            ChannelId = channelId,
            MeetingId = meetingId,
            ParentMessageId = parentMessageId,
            Body = text,
            CreatedAt = now,
        };
        message.CaptureLinkPreviewStub();
        foreach (var fileId in attachmentFileIds.Distinct())
            message.Attachments.Add(new Attachment(message.Id, fileId));
        return message;
    }

    public void Edit(string? body, DateTimeOffset now)
    {
        EnsureMutable();
        Body = NormalizeBody(body, requireContent: Attachments.Count == 0);
        EditedAt = now;
        CaptureLinkPreviewStub();
    }

    /// <summary>Lightweight preview: first http(s) URL becomes a hostname chip; OG fetch is deferred.</summary>
    public void CaptureLinkPreviewStub()
    {
        LinkPreviewUrl = LinkPreviewTitle = LinkPreviewDescription = null;
        foreach (var token in Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(token.TrimEnd('.', ',', ')', ']'), UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;
            LinkPreviewUrl = uri.AbsoluteUri;
            LinkPreviewTitle = uri.Host;
            LinkPreviewDescription = uri.AbsolutePath == "/" ? null : uri.AbsolutePath;
            break;
        }
    }

    public void SoftDelete(DateTimeOffset now)
    {
        EnsureMutable();
        Body = string.Empty;
        DeletedAt = now;
        EditedAt = null;
    }

    public void AddReply() => ReplyCount++;

    public void RemoveReply()
    {
        if (ReplyCount > 0) ReplyCount--;
    }

    /// <summary>One-line summary for conversation lists.</summary>
    public string Preview()
    {
        if (IsDeleted) return "Message deleted";
        if (Body.Length == 0)
            return Attachments.Count == 1 ? "Sent an attachment" : $"Sent {Attachments.Count} attachments";

        var singleLine = string.Join(' ', Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= MaxPreviewLength ? singleLine : singleLine[..(MaxPreviewLength - 1)] + "…";
    }

    private void EnsureMutable()
    {
        if (IsDeleted) throw new DomainException("That message was deleted.");
    }

    private static string NormalizeBody(string? body, bool requireContent)
    {
        var text = body?.Trim() ?? string.Empty;
        if (requireContent && text.Length == 0)
            throw new DomainException("A message needs text or at least one attachment.");
        if (text.Length > MaxBodyLength)
            throw new DomainException($"A message must be {MaxBodyLength} characters or fewer.");
        return text;
    }
}

/// <summary>One emoji reaction from one user on one message. Unique per (message, user, emoji).</summary>
public class MessageReaction
{
    public const int MaxEmojiLength = 32;

    private MessageReaction() { }

    public Guid MessageId { get; private set; }
    public Guid UserId { get; private set; }
    public string Emoji { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public Message Message { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public static MessageReaction Create(Guid messageId, Guid userId, string emoji, DateTimeOffset now)
    {
        var clean = Guard.Required(emoji, "Emoji", MaxEmojiLength);
        if (clean.Any(char.IsWhiteSpace))
            throw new DomainException("An emoji reaction cannot contain spaces.");

        return new MessageReaction
        {
            MessageId = messageId,
            UserId = userId,
            Emoji = clean,
            CreatedAt = now,
        };
    }
}

/// <summary>A per-recipient feed item for replies, reactions, and mentions.</summary>
public class ActivityItem
{
    public const int MaxSummaryLength = 200;

    private ActivityItem() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RecipientId { get; private set; }
    public Guid ActorId { get; private set; }
    public ActivityKind Kind { get; private set; }
    public Guid? MessageId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public Guid? ChannelId { get; private set; }
    public Guid? MeetingId { get; private set; }
    public Guid? CallId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public User Recipient { get; private set; } = null!;
    public User Actor { get; private set; } = null!;

    public bool IsUnread => ReadAt is null;

    public static ActivityItem Create(
        Guid organizationId,
        Guid recipientId,
        Guid actorId,
        ActivityKind kind,
        Guid? messageId,
        Guid? conversationId,
        Guid? channelId,
        string summary,
        DateTimeOffset now,
        Guid? meetingId = null,
        Guid? callId = null)
    {
        if (recipientId == actorId)
            throw new DomainException("Activity is not created for the actor.");

        return new ActivityItem
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            RecipientId = recipientId,
            ActorId = actorId,
            Kind = kind,
            MessageId = messageId,
            ConversationId = conversationId,
            ChannelId = channelId,
            MeetingId = meetingId,
            CallId = callId,
            Summary = Guard.Required(summary, "Summary", MaxSummaryLength),
            CreatedAt = now,
        };
    }

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}

public class Attachment(Guid messageId, Guid fileObjectId)
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid MessageId { get; private set; } = messageId;
    public Guid FileObjectId { get; private set; } = fileObjectId;

    public FileObject FileObject { get; private set; } = null!;
}

/// <summary>A stored file. Scoped to the conversation or channel it was shared in, which decides who can read it.</summary>
public class FileObject
{
    public const long MaxSizeBytes = 50L * 1024 * 1024;
    public const int MaxFileNameLength = 255;

    private FileObject() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid UploaderId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public Guid? ChannelId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public User Uploader { get; private set; } = null!;

    public static FileObject Create(
        Guid organizationId,
        Guid uploaderId,
        Guid? conversationId,
        Guid? channelId,
        string? fileName,
        string? contentType,
        long size,
        DateTimeOffset now)
    {
        if (conversationId is null == channelId is null)
            throw new DomainException("A file is shared in exactly one conversation or channel.");
        if (size <= 0) throw new DomainException("The file is empty.");
        if (size > MaxSizeBytes)
            throw new DomainException($"Files can be at most {MaxSizeBytes / (1024 * 1024)} MB.");

        var id = Guid.CreateVersion7();
        return new FileObject
        {
            Id = id,
            OrganizationId = organizationId,
            UploaderId = uploaderId,
            ConversationId = conversationId,
            ChannelId = channelId,
            FileName = SanitizeFileName(fileName),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            Size = size,
            // The key never contains user input, so a crafted file name cannot escape the bucket prefix.
            StorageKey = $"{organizationId:N}/{id:N}",
            CreatedAt = now,
        };
    }

    /// <summary>Reduces a client-supplied name to a safe display name: no directories, no control characters.</summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim().Trim('.');
        if (name.Length == 0) return "file";
        return name.Length <= MaxFileNameLength ? name : name[..MaxFileNameLength];
    }
}
