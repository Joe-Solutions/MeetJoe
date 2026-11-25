using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

/// <summary>Bubble row for in-meeting / in-call chat (DM-style left/right layout).</summary>
public partial class RoomChatMessageViewModel : ObservableObject
{
    public RoomChatMessageViewModel(MessageDto message, SessionContext context)
    {
        Id = message.Id;
        CreatedAt = message.CreatedAt;
        Body = message.Body ?? string.Empty;
        Sender = context.People.Get(message.Sender);
        IsMine = message.Sender.Id == context.People.MyId;
        TimeText = message.CreatedAt.ToLocalTime().ToString("h:mm tt");
        StartsGroup = true;
        Attachments = (message.Attachments ?? [])
            .Select(a => new AttachmentViewModel(a, context))
            .ToList();
    }

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public string Body { get; }
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public PersonViewModel Sender { get; }
    public bool IsMine { get; }
    public string TimeText { get; }
    public bool StartsGroup { get; set; }
    public bool IsContinuation => !StartsGroup;
    public bool ShowAvatar => StartsGroup && !IsMine;
    public IReadOnlyList<AttachmentViewModel> Attachments { get; }
    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSeenBy))]
    private string? _seenByText;

    public bool ShowSeenBy => !string.IsNullOrEmpty(SeenByText);

    public static readonly IReadOnlyList<string> EmojiPalette =
        ["😀", "😂", "🙂", "😍", "🤔", "👍", "👎", "👏", "🙏", "🎉", "🔥", "✅", "❌", "💡", "📌"];

    /// <summary>Mark consecutive same-sender rows so avatar/name are not repeated.</summary>
    public static void ApplyGrouping(IList<RoomChatMessageViewModel> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (i == 0)
            {
                messages[i].StartsGroup = true;
                continue;
            }

            var previous = messages[i - 1];
            var current = messages[i];
            current.StartsGroup = previous.Sender.Id != current.Sender.Id || previous.IsMine != current.IsMine;
        }
    }

    public static bool TryAdd(
        ObservableCollection<RoomChatMessageViewModel> messages,
        MessageDto dto,
        SessionContext context)
    {
        if (messages.Any(m => m.Id == dto.Id)) return false;
        var item = new RoomChatMessageViewModel(dto, context);
        if (messages.Count > 0)
        {
            var previous = messages[^1];
            item.StartsGroup = previous.Sender.Id != item.Sender.Id || previous.IsMine != item.IsMine;
        }

        messages.Add(item);
        return true;
    }

    /// <summary>Updates the Seen line on the latest outgoing message in a room chat list.</summary>
    public static void RefreshSeenBy(
        IList<RoomChatMessageViewModel> messages,
        Guid myId,
        IEnumerable<(Guid UserId, string DisplayName)> peers,
        IReadOnlyDictionary<Guid, DateTimeOffset> readAt)
    {
        foreach (var message in messages)
            message.SeenByText = null;

        var latest = messages.LastOrDefault();
        if (latest is null || !latest.IsMine) return;

        latest.SeenByText = Services.SeenBy.Format(latest.CreatedAt, myId, peers, readAt);
    }
}
