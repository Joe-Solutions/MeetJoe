using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Services;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public partial class AttachmentViewModel : ObservableObject
{
    private readonly SessionContext _context;
    private readonly Guid _fileId;
    private readonly string _contentType;

    public AttachmentViewModel(AttachmentDto attachment, SessionContext context)
    {
        _context = context;
        _fileId = attachment.FileId;
        _contentType = attachment.ContentType;
        FileName = attachment.FileName;
        SizeText = Describe.FileSize(attachment.Size);
        IsImage = FileTypes.IsImage(attachment.ContentType, attachment.FileName);
        if (IsImage || LooksLikeOpaqueImage(attachment))
            _ = LoadPreviewAsync();
    }

    public string FileName { get; }
    public string SizeText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileChip))]
    private bool _isImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileChip))]
    private Bitmap? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileChip))]
    private bool _isLoadingPreview;

    [ObservableProperty] private string? _status;

    public bool ShowFileChip => !IsImage || (Preview is null && !IsLoadingPreview);

    /// <summary>GUID-named Photos uploads often arrive as octet-stream with no extension.</summary>
    private static bool LooksLikeOpaqueImage(AttachmentDto attachment) =>
        attachment.ContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
        && attachment.Size is > 0 and < 20L * 1024 * 1024;

    private async Task LoadPreviewAsync()
    {
        IsLoadingPreview = true;
        try
        {
            await using var buffer = new MemoryStream();
            await _context.Api.DownloadFileAsync(_fileId, buffer);
            if (buffer.Length == 0) return;
            buffer.Position = 0;

            if (!IsImage)
            {
                Span<byte> header = stackalloc byte[12];
                var read = buffer.Read(header);
                buffer.Position = 0;
                if (FileTypes.SniffImage(header[..read]) is null) return;
                IsImage = true;
            }

            var bitmap = new Bitmap(buffer);
            await Dispatcher.UIThread.InvokeAsync(() => Preview = bitmap);
        }
        catch
        {
            // Keep the file chip when the bytes are not a displayable image.
            IsImage = FileTypes.IsImage(_contentType, FileName);
            Preview = null;
        }
        finally
        {
            IsLoadingPreview = false;
        }
    }

    [RelayCommand]
    private async Task OpenFullscreenAsync()
    {
        if (Preview is null) return;
        await _context.Platform.ShowImageAsync(Preview);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        try
        {
            await using var destination = await _context.Platform.OpenSaveStreamAsync(FileName);
            if (destination is null) return;

            Status = "Downloading…";
            await _context.Api.DownloadFileAsync(_fileId, destination);
            Status = "Saved";
        }
        catch (Exception ex)
        {
            Status = Describe.Error(ex);
        }
    }
}

public partial class ReactionChipViewModel(Guid messageId, ReactionSummaryDto reaction) : ObservableObject
{
    public Guid MessageId { get; } = messageId;
    public string Emoji { get; } = reaction.Emoji;
    public int Count { get; } = reaction.Count;
    public bool ReactedByMe { get; } = reaction.ReactedByMe;
    public string Label => $"{Emoji} {Count}";
}

public partial class MessageViewModel : ObservableObject
{
    public static readonly string[] QuickReactions = ["👍", "❤️", "😂", "🎉", "🔥"];

    private MessageThreadViewModel? _thread;

    public MessageViewModel(MessageDto message, SessionContext context, MessageThreadViewModel? thread = null)
    {
        _thread = thread;
        Id = message.Id;
        CreatedAt = message.CreatedAt;
        Sender = context.People.Get(message.Sender);
        IsMine = message.Sender.Id == context.People.MyId;
        TimeText = message.CreatedAt.ToLocalTime().ToString("h:mm tt");
        Attachments = message.Attachments.Select(a => new AttachmentViewModel(a, context)).ToList();
        Apply(message);
    }

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public PersonViewModel Sender { get; }
    public bool IsMine { get; }
    public string TimeText { get; }
    public IReadOnlyList<AttachmentViewModel> Attachments { get; }
    public IReadOnlyList<string> ReactionChoices => QuickReactions;

    internal void Attach(MessageThreadViewModel thread) => _thread = thread;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody), nameof(DisplayBody))]
    private string _body = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEdited))]
    private DateTimeOffset? _editedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody), nameof(DisplayBody), nameof(CanMutate), nameof(ShowActions))]
    private bool _isDeleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplies), nameof(RepliesLabel))]
    private int _replyCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReactions))]
    private IReadOnlyList<ReactionChipViewModel> _reactions = [];

    /// <summary>Hover emoji icon opens this picker beside the message (not over the bubble).</summary>
    [ObservableProperty]
    private bool _showReactionPicker;

    /// <summary>False when this continues a run from the same sender, so the name and avatar are not repeated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAvatar), nameof(IsContinuation))]
    private bool _startsGroup = true;

    /// <summary>Other people's avatar — only on the first message in a run. Own messages never show one.</summary>
    public bool ShowAvatar => StartsGroup && !IsMine;

    public bool IsContinuation => !StartsGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private string? _editDraft;

    public bool HasBody => !IsDeleted && Body.Length > 0;
    public string DisplayBody => IsDeleted ? "This message was deleted." : Body;
    public bool ShowEdited => !IsDeleted && EditedAt is not null;
    public bool HasReplies => ReplyCount > 0;
    public string RepliesLabel => ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies";
    public bool HasReactions => Reactions.Count > 0;
    public bool CanMutate => IsMine && !IsDeleted;
    public bool ShowActions => !IsDeleted;
    public bool IsEditing => EditDraft is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkPreview))]
    private LinkPreviewDto? _linkPreview;

    public bool HasLinkPreview => !IsDeleted && LinkPreview is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSeenBy))]
    private string? _seenByText;

    public bool ShowSeenBy => !string.IsNullOrEmpty(SeenByText);

    public void Apply(MessageDto message)
    {
        Body = message.Body;
        EditedAt = message.EditedAt;
        IsDeleted = message.IsDeleted;
        ReplyCount = message.ReplyCount;
        Reactions = (message.Reactions ?? []).Select(r => new ReactionChipViewModel(Id, r)).ToList();
        LinkPreview = message.LinkPreview;
        EditDraft = null;
    }

    [RelayCommand]
    private void BeginReply()
    {
        ShowReactionPicker = false;
        _thread?.BeginReplyCommand.Execute(this);
    }

    [RelayCommand]
    private void BeginEdit()
    {
        ShowReactionPicker = false;
        _thread?.BeginEditCommand.Execute(this);
    }

    [RelayCommand]
    private void CancelEdit() => _thread?.CancelEditCommand.Execute(this);

    [RelayCommand]
    private Task SaveEditAsync() => _thread?.SaveEditCommand.ExecuteAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeleteAsync() => _thread?.DeleteCommand.ExecuteAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenRepliesAsync() => _thread?.OpenRepliesCommand.ExecuteAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void ToggleReactionPicker()
    {
        var open = !ShowReactionPicker;
        _thread?.CloseReactionPickers(except: open ? this : null);
        ShowReactionPicker = open;
    }

    [RelayCommand]
    private async Task ReactAsync(string emoji)
    {
        ShowReactionPicker = false;
        await (_thread?.ReactToAsync(this, emoji) ?? Task.CompletedTask);
    }

    [RelayCommand]
    private Task ToggleChipAsync(ReactionChipViewModel chip) =>
        _thread?.ReactToAsync(this, chip.Emoji) ?? Task.CompletedTask;
}

public partial class PendingAttachmentViewModel(FileDto file) : ObservableObject
{
    public Guid FileId { get; } = file.Id;
    public string FileName { get; } = file.FileName;
    public string SizeText { get; } = Describe.FileSize(file.Size);
    public bool IsImage { get; } = FileTypes.IsImage(file.ContentType, file.FileName);
}

/// <summary>An @mention picker row — either @everyone or a group member.</summary>
public sealed class MentionSuggestion(string token, string label, string detail, PersonViewModel? person = null)
{
    /// <summary>Text inserted after @ (e.g. everyone or Display Name).</summary>
    public string Token { get; } = token;
    public string Label { get; } = label;
    public string Detail { get; } = detail;
    public PersonViewModel? Person { get; } = person;
    public bool IsEveryone => Person is null;
}

/// <summary>The message list and composer for one conversation or channel.</summary>
public partial class MessageThreadViewModel : ViewModelBase
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(5);

    private readonly SessionContext _context;
    private readonly Guid? _conversationId;
    private readonly Guid? _channelId;
    private readonly IReadOnlyList<PersonViewModel> _mentionables;
    private readonly HashSet<Guid> _knownIds = [];
    private readonly Dictionary<Guid, DateTimeOffset> _readAt = [];
    private List<(Guid UserId, string DisplayName)> _seenPeers = [];
    private CancellationTokenSource? _typingIdle;

    public MessageThreadViewModel(
        SessionContext context,
        Guid? conversationId,
        Guid? channelId,
        string title,
        string subtitle,
        IReadOnlyList<PersonViewModel>? mentionables = null)
    {
        _context = context;
        _conversationId = conversationId;
        _channelId = channelId;
        _mentionables = mentionables ?? [];
        _title = title;
        _subtitle = subtitle;
    }

    /// <summary>Members whose read cursors drive the Seen / Seen by everyone line.</summary>
    public void ConfigureSeenBy(
        IEnumerable<PersonViewModel> peers,
        IEnumerable<ReadCursorDto>? cursors = null)
    {
        _seenPeers = peers
            .Select(p => (p.Id, p.DisplayName))
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .ToList();
        _readAt.Clear();
        if (cursors is not null)
        {
            foreach (var cursor in cursors)
                _readAt[cursor.UserId] = cursor.ReadAt;
        }

        RefreshSeenBy();
    }

    public void ApplyReadReceipt(Guid userId, DateTimeOffset readAt, string? displayName = null)
    {
        if (_readAt.TryGetValue(userId, out var existing) && existing >= readAt) return;
        _readAt[userId] = readAt;
        if (displayName is not null && _seenPeers.All(p => p.UserId != userId))
            _seenPeers.Add((userId, displayName));
        RefreshSeenBy();
    }

    private void RefreshSeenBy()
    {
        foreach (var message in Messages)
            message.SeenByText = null;

        var latest = Messages.LastOrDefault(m => !m.IsDeleted);
        if (latest is null || !latest.IsMine) return;

        latest.SeenByText = SeenBy.Format(
            latest.CreatedAt, _context.People.MyId, _seenPeers, _readAt);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private string _title;

    [ObservableProperty] private string _subtitle;

    public bool IsChannel => _channelId is not null;
    public bool SupportsMentions => _mentionables.Count > 0;
    public string Placeholder => ReplyTarget is { } target
        ? $"Reply to {target.Sender.DisplayName}"
        : IsChannel ? $"Post in {Title}" : $"Message {Title}";

    public void SetHeader(string title, string? subtitle = null)
    {
        Title = title;
        if (subtitle is not null) Subtitle = subtitle;
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<MessageViewModel> ThreadReplies { get; } = [];
    public ObservableCollection<PendingAttachmentViewModel> PendingAttachments { get; } = [];
    public ObservableCollection<MentionSuggestion> MentionSuggestions { get; } = [];

    /// <summary>Raised after a message is appended so the view can keep the newest in sight.</summary>
    public event Action? MessageAppended;

    /// <summary>Raised when the user taps the “New messages” chip — view scrolls to the latest.</summary>
    public event Action? ScrollToLatestRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    /// <summary>Floating chip above the composer when new messages arrived while scrolled up.</summary>
    [ObservableProperty] private bool _showNewMessagesHint;

    [ObservableProperty] private bool _hasOlder;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _typingText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ShowMentionSuggestions))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMentionSuggestions))]
    private bool _hasMentionSuggestions;

    public bool ShowMentionSuggestions => SupportsMentions && HasMentionSuggestions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder), nameof(IsReplying), nameof(ShowThreadPanel))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private MessageViewModel? _replyTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThreadPanel))]
    private MessageViewModel? _openThread;

    [ObservableProperty] private bool _isLoadingReplies;

    public bool IsEmpty => !IsLoading && Messages.Count == 0;
    public bool IsReplying => ReplyTarget is not null;
    public bool ShowThreadPanel => OpenThread is not null;

    public bool Matches(MessageDto message) =>
        message.ConversationId == _conversationId && message.ChannelId == _channelId
        && message.MeetingId is null;

    public void ApplyTyping(TypingDto typing)
    {
        if (typing.ConversationId != _conversationId || typing.ChannelId != _channelId) return;
        if (typing.UserId == _context.People.MyId) return;
        TypingText = typing.IsTyping ? $"{typing.DisplayName} is typing…" : null;
    }

    partial void OnDraftChanged(string value)
    {
        RefreshMentionSuggestions(value);
        _ = NotifyTypingAsync(value);
    }

    private void RefreshMentionSuggestions(string value)
    {
        MentionSuggestions.Clear();
        if (!SupportsMentions)
        {
            HasMentionSuggestions = false;
            return;
        }

        // Incomplete @query at the end of the draft (Slack-style).
        var at = value.LastIndexOf('@');
        if (at < 0 || (at > 0 && !char.IsWhiteSpace(value[at - 1])))
        {
            HasMentionSuggestions = false;
            return;
        }

        var query = value[(at + 1)..];
        if (query.Contains('\n'))
        {
            HasMentionSuggestions = false;
            return;
        }

        if ("everyone".StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            MentionSuggestions.Add(new MentionSuggestion(
                "everyone", "@everyone", "Notify everyone in this group"));
        }

        foreach (var person in _mentionables
                     .Where(p => query.Length == 0
                                 || p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .Take(8))
        {
            MentionSuggestions.Add(new MentionSuggestion(
                person.DisplayName, person.DisplayName, person.Email, person));
        }

        HasMentionSuggestions = MentionSuggestions.Count > 0;
    }

    [RelayCommand]
    private void PickMention(MentionSuggestion suggestion)
    {
        if (suggestion is null) return;
        var value = Draft;
        var at = value.LastIndexOf('@');
        if (at < 0) return;
        Draft = value[..(at + 1)] + suggestion.Token + " ";
        MentionSuggestions.Clear();
        HasMentionSuggestions = false;
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task NotifyTypingAsync(string value)
    {
        if (!_context.Realtime.IsConnected) return;
        _typingIdle?.Cancel();
        _typingIdle = new CancellationTokenSource();
        var ct = _typingIdle.Token;
        try
        {
            await _context.Realtime.SetTypingAsync(_conversationId, _channelId, !string.IsNullOrWhiteSpace(value));
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await _context.Realtime.SetTypingAsync(_conversationId, _channelId, false);
        }
        catch (OperationCanceledException)
        {
            // Newer keystroke reset the idle timer.
        }
        catch (Exception)
        {
            // Typing is best-effort.
        }
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var page = await _context.Api.GetMessagesAsync(_conversationId, _channelId, null);
            Messages.Clear();
            _knownIds.Clear();
            foreach (var message in page) Add(message, atEnd: true);
            HasOlder = page.Count >= 50;
            if (OpenThread is { } open)
            {
                var refreshed = Messages.FirstOrDefault(m => m.Id == open.Id);
                if (refreshed is null) CloseThread();
                else await OpenRepliesAsync(refreshed);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            RefreshSeenBy();
            MessageAppended?.Invoke();
        }
    }

    [RelayCommand]
    private async Task LoadOlderAsync()
    {
        if (Messages.Count == 0) return;
        try
        {
            var page = await _context.Api.GetMessagesAsync(_conversationId, _channelId, Messages[0].CreatedAt);
            for (var i = page.Count - 1; i >= 0; i--) Add(page[i], atEnd: false);
            HasOlder = page.Count >= 50;
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    /// <summary>Adds a root message that arrived live. Safe to call for one already shown.</summary>
    public void Append(MessageDto message)
    {
        if (message.ParentMessageId is { } parentId)
        {
            var parent = Messages.FirstOrDefault(m => m.Id == parentId);
            if (parent is not null) parent.ReplyCount++;
            if (OpenThread?.Id == parentId) AddReply(message);
            return;
        }

        if (!Add(message, atEnd: true)) return;
        OnPropertyChanged(nameof(IsEmpty));
        RefreshSeenBy();
        MessageAppended?.Invoke();
    }

    public void ApplyUpdate(MessageDto message)
    {
        if (message.ParentMessageId is { } parentId)
        {
            if (OpenThread?.Id == parentId)
            {
                var reply = ThreadReplies.FirstOrDefault(m => m.Id == message.Id);
                if (reply is not null) reply.Apply(message);
                else if (!message.IsDeleted) AddReply(message);
            }

            var parent = Messages.FirstOrDefault(m => m.Id == parentId);
            if (parent is not null && message.IsDeleted && parent.ReplyCount > 0) parent.ReplyCount--;
            return;
        }

        var existing = Messages.FirstOrDefault(m => m.Id == message.Id);
        if (existing is not null) existing.Apply(message);
        if (OpenThread?.Id == message.Id)
        {
            OpenThread.Apply(message);
            if (message.IsDeleted) CloseThread();
        }

        if (message.IsDeleted) RefreshSeenBy();
    }

    public IReadOnlyList<string> EmojiPalette { get; } =
        ["😀", "😂", "🙂", "😍", "🤔", "👍", "👎", "👏", "🙏", "🎉", "🔥", "✅", "❌", "💡", "📌"];

    [ObservableProperty] private bool _showEmojiPicker;

    private bool CanSend() =>
        (!string.IsNullOrWhiteSpace(Draft) || PendingAttachments.Count > 0)
        && (ReplyTarget is null || !ReplyTarget.IsDeleted);

    [RelayCommand]
    private void WrapMarkdown(string marker)
    {
        if (string.IsNullOrWhiteSpace(marker)) return;
        if (marker == "codeblock")
        {
            Draft = string.IsNullOrWhiteSpace(Draft) ? "```\ncode\n```" : Draft + "\n```\ncode\n```";
            return;
        }

        var selected = Draft.Trim();
        Draft = selected.Length == 0
            ? marker == "`" ? "`code`" : $"{marker}text{marker}"
            : $"{marker}{selected}{marker}";
    }

    [RelayCommand]
    private void ToggleEmojiPicker() => ShowEmojiPicker = !ShowEmojiPicker;

    [RelayCommand]
    private void InsertEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        Draft += emoji;
        ShowEmojiPicker = false;
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var body = Draft;
        var attachments = PendingAttachments.ToList();
        var parentId = ReplyTarget?.Id ?? OpenThread?.Id;
        var request = new SendMessageRequest(
            _conversationId, _channelId, body, attachments.Select(a => a.FileId).ToList(), parentId);

        // Clear first so a second Enter cannot send the same text twice; restored on failure.
        Draft = string.Empty;
        PendingAttachments.Clear();
        var replyTarget = ReplyTarget;
        ReplyTarget = null;
        ErrorMessage = null;
        try
        {
            var sent = _context.Realtime.IsConnected
                ? await _context.Realtime.SendMessageAsync(request)
                : await _context.Api.SendMessageAsync(request);
            Append(sent);
        }
        catch (Exception ex)
        {
            Draft = body;
            foreach (var attachment in attachments) PendingAttachments.Add(attachment);
            ReplyTarget = replyTarget;
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task AttachAsync()
    {
        ErrorMessage = null;
        try
        {
            var picked = await _context.Platform.PickFileAsync();
            if (picked is null) return;
            await AttachFilesAsync([picked]);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Upload one or more files from the Attach button or a drag-drop onto the composer.</summary>
    public async Task AttachFilesAsync(IReadOnlyList<PickedFile> files)
    {
        if (files.Count == 0) return;
        ErrorMessage = null;
        try
        {
            foreach (var picked in files)
            {
                await using var content = await picked.OpenReadAsync();
                var file = await _context.Api.UploadFileAsync(_conversationId, _channelId, picked.Name, content);
                PendingAttachments.Add(new PendingAttachmentViewModel(file));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void RemoveAttachment(PendingAttachmentViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>View should focus the main composer (after Reply).</summary>
    public event Action? FocusComposerRequested;

    /// <summary>View should focus the in-place edit box for the message being edited.</summary>
    public event Action? FocusEditRequested;

    [RelayCommand]
    private void BeginReply(MessageViewModel message)
    {
        if (message.IsDeleted) return;
        ReplyTarget = message;
        OpenThread = message;
        _ = OpenRepliesAsync(message);
        FocusComposerRequested?.Invoke();
    }

    [RelayCommand]
    private void CancelReply() => ReplyTarget = null;

    /// <summary>Called by the view when a live message arrived while the reader was scrolled up.</summary>
    public void NotifyUnseenMessages() => ShowNewMessagesHint = true;

    public void ClearNewMessagesHint() => ShowNewMessagesHint = false;

    [RelayCommand]
    private void JumpToLatest()
    {
        ShowNewMessagesHint = false;
        ScrollToLatestRequested?.Invoke();
    }

    [RelayCommand]
    private async Task OpenRepliesAsync(MessageViewModel message)
    {
        if (message.IsDeleted) return;
        OpenThread = message;
        IsLoadingReplies = true;
        ErrorMessage = null;
        try
        {
            var replies = await _context.Api.GetRepliesAsync(message.Id);
            ThreadReplies.Clear();
            foreach (var reply in replies) AddReply(reply);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsLoadingReplies = false;
        }
    }

    [RelayCommand]
    private void CloseThread()
    {
        OpenThread = null;
        ThreadReplies.Clear();
        if (ReplyTarget is not null && Messages.All(m => m.Id != ReplyTarget.Id))
            ReplyTarget = null;
    }

    [RelayCommand]
    private void BeginEdit(MessageViewModel message)
    {
        if (!message.CanMutate) return;
        // Only one message edits at a time.
        foreach (var other in Messages.Where(m => m.IsEditing && !ReferenceEquals(m, message)))
            other.EditDraft = null;
        message.EditDraft = message.Body;
        FocusEditRequested?.Invoke();
    }

    [RelayCommand]
    private void CancelEdit(MessageViewModel message) => message.EditDraft = null;

    [RelayCommand]
    private async Task SaveEditAsync(MessageViewModel message)
    {
        if (message.EditDraft is null) return;
        var body = message.EditDraft;
        ErrorMessage = null;
        try
        {
            var updated = _context.Realtime.IsConnected
                ? await _context.Realtime.EditMessageAsync(message.Id, body)
                : await _context.Api.EditMessageAsync(message.Id, body);
            message.Apply(updated);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(MessageViewModel message)
    {
        if (!message.CanMutate) return;
        ErrorMessage = null;
        try
        {
            var updated = _context.Realtime.IsConnected
                ? await _context.Realtime.DeleteMessageAsync(message.Id)
                : await _context.Api.DeleteMessageAsync(message.Id);
            ApplyUpdate(updated);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    internal void CloseReactionPickers(MessageViewModel? except = null)
    {
        foreach (var message in Messages)
        {
            if (ReferenceEquals(message, except)) continue;
            message.ShowReactionPicker = false;
        }
    }

    internal async Task ReactToAsync(MessageViewModel message, string emoji)
    {
        if (message.IsDeleted || string.IsNullOrWhiteSpace(emoji)) return;
        ErrorMessage = null;
        try
        {
            var updated = _context.Realtime.IsConnected
                ? await _context.Realtime.ToggleReactionAsync(message.Id, emoji)
                : await _context.Api.ToggleReactionAsync(message.Id, emoji);
            ApplyUpdate(updated);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    private bool Add(MessageDto message, bool atEnd)
    {
        if (!_knownIds.Add(message.Id)) return false;

        var item = new MessageViewModel(message, _context, this);
        if (atEnd)
        {
            item.StartsGroup = !Continues(Messages.LastOrDefault(), item);
            Messages.Add(item);
        }
        else
        {
            if (Messages.Count > 0) Messages[0].StartsGroup = !Continues(item, Messages[0]);
            Messages.Insert(0, item);
        }

        return true;
    }

    private void AddReply(MessageDto message)
    {
        if (ThreadReplies.Any(m => m.Id == message.Id))
        {
            ThreadReplies.First(m => m.Id == message.Id).Apply(message);
            return;
        }

        ThreadReplies.Add(new MessageViewModel(message, _context, this) { StartsGroup = true });
    }

    private static bool Continues(MessageViewModel? previous, MessageViewModel next) =>
        previous is not null
        && previous.Sender.Id == next.Sender.Id
        && next.CreatedAt - previous.CreatedAt < GroupWindow;
}
