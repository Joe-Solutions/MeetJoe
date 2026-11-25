using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public partial class ConversationViewModel : ObservableObject
{
    private readonly PeopleStore _people;
    private string? _storedTitle;

    public ConversationViewModel(ConversationDto conversation, PeopleStore people)
    {
        _people = people;
        Id = conversation.Id;
        IsDirect = conversation.Kind == ConversationKind.Direct;
        _storedTitle = conversation.Title;
        Other = IsDirect
            ? conversation.Members.Select(people.Get).FirstOrDefault(m => !m.IsMe)
            : null;
        ApplyMembers(conversation);
        Update(conversation);
    }

    public Guid Id { get; }
    public bool IsDirect { get; }
    public bool IsGroup => !IsDirect;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;

    public ObservableCollection<PersonViewModel> Members { get; } = [];

    /// <summary>The other person in a direct chat, whose presence the list shows.</summary>
    public PersonViewModel? Other { get; }

    /// <summary>Person whose photo/initials represent this conversation in the list.</summary>
    public PersonViewModel ListAvatar =>
        Other ?? Members.FirstOrDefault(m => !m.IsMe) ?? Members.FirstOrDefault()
        ?? new PersonViewModel(Guid.Empty, Title, false);

    [ObservableProperty] private string _preview = string.Empty;
    [ObservableProperty] private string _timeText = string.Empty;
    [ObservableProperty] private DateTimeOffset? _lastMessageAt;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    [ObservableProperty] private bool _isSelected;

    public void Update(ConversationDto conversation)
    {
        _storedTitle = conversation.Title;
        Preview = conversation.LastMessagePreview ?? "No messages yet";
        LastMessageAt = conversation.LastMessageAt;
        TimeText = conversation.LastMessageAt is { } at ? Describe.Time(at) : string.Empty;
        UnreadCount = conversation.UnreadCount;
        IsMuted = conversation.IsMuted;
        IsPinned = conversation.IsPinned;
        ApplyMembers(conversation);
    }

    private void ApplyMembers(ConversationDto conversation)
    {
        var next = conversation.Members.Select(_people.Get).ToList();
        for (var i = Members.Count - 1; i >= 0; i--)
        {
            if (next.All(p => p.Id != Members[i].Id))
                Members.RemoveAt(i);
        }

        foreach (var person in next)
        {
            if (Members.All(m => m.Id != person.Id))
                Members.Add(person);
        }

        var others = Members.Where(m => !m.IsMe).ToList();
        Title = IsDirect
            ? Other?.DisplayName ?? "Direct chat"
            : !string.IsNullOrWhiteSpace(_storedTitle)
                ? _storedTitle
                : string.Join(", ", others.Select(m => m.DisplayName));
        Subtitle = IsDirect ? "Direct chat" : $"{Members.Count} people";
    }

    public void RecordMessage(MessageDto message, bool countAsUnread)
    {
        if (message.ParentMessageId is not null) return;

        Preview = message.IsDeleted
            ? "Message deleted"
            : message.Body.Length > 0
                ? message.Body
                : message.Attachments.Count == 1
                    ? "Sent an attachment"
                    : $"Sent {message.Attachments.Count} attachments";
        LastMessageAt = message.CreatedAt;
        TimeText = Describe.Time(message.CreatedAt);
        if (countAsUnread) UnreadCount++;
    }
}

/// <summary>1:1 and group chat: the conversation list, the open thread, and starting new chats.</summary>
public partial class ChatViewModel : ViewModelBase
{
    private readonly SessionContext _context;

    public ChatViewModel(SessionContext context)
    {
        _context = context;
        context.Realtime.MessageReceived += OnMessageReceived;
        context.Realtime.MessageUpdated += OnMessageUpdated;
        context.Realtime.Typing += OnTyping;
        context.Realtime.ConversationsChanged += OnConversationsChanged;
    }

    public ObservableCollection<ConversationViewModel> Conversations { get; } = [];

    /// <summary>Everyone else in the organization, for the new-chat picker.</summary>
    public ObservableCollection<PersonViewModel> Candidates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoConversations))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private MessageThreadViewModel? _thread;
    [ObservableProperty] private bool _isMembersPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowEmptyState), nameof(CanManageMembers))]
    private ConversationViewModel? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isStartingChat;
    [ObservableProperty] private string _newChatTitle = string.Empty;
    [ObservableProperty] private string? _newChatError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemberSuggestions))]
    private string _memberEmailQuery = string.Empty;

    [ObservableProperty] private string? _memberManageStatus;

    public ObservableCollection<PersonViewModel> MemberSuggestions { get; } = [];

    public bool HasSelection => Selected is not null;
    public bool ShowEmptyState => Selected is null && !IsStartingChat;
    public bool HasNoConversations => !IsLoading && Conversations.Count == 0;
    public bool CanManageMembers => Selected?.IsGroup == true;
    public bool HasMemberSuggestions => MemberSuggestions.Count > 0;

    /// <summary>Total unread across conversations, shown on the rail.</summary>
    public int UnreadTotal => Conversations.Sum(c => c.UnreadCount);

    public event Action? UnreadChanged;

    /// <summary>Raised when the user starts a VoIP call from the open conversation.</summary>
    public Func<Guid, Task>? StartCallHandler { get; set; }

    public void Detach()
    {
        _context.Realtime.MessageReceived -= OnMessageReceived;
        _context.Realtime.MessageUpdated -= OnMessageUpdated;
        _context.Realtime.Typing -= OnTyping;
        _context.Realtime.ConversationsChanged -= OnConversationsChanged;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var conversations = await _context.Api.GetConversationsAsync(_context.Organization.Id);
            var selectedId = Selected?.Id;

            var existing = Conversations.ToDictionary(c => c.Id);
            Conversations.Clear();
            foreach (var dto in conversations)
            {
                if (existing.TryGetValue(dto.Id, out var item)) item.Update(dto);
                else item = new ConversationViewModel(dto, _context.People);
                Conversations.Add(item);
            }

            // Existing items are reused, so the open conversation stays selected across a refresh.
            if (selectedId is { } id)
            {
                if (Conversations.FirstOrDefault(c => c.Id == id) is { } open) open.UnreadCount = 0;
                else Selected = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoConversations));
            RaiseUnreadChanged();
        }
    }

    [RelayCommand]
    private void Select(ConversationViewModel conversation) => Selected = conversation;

    /// <summary>Opens a conversation by id (e.g. from call history). Loads the list if needed.</summary>
    public async Task OpenConversationAsync(Guid conversationId)
    {
        if (Conversations.All(c => c.Id != conversationId))
            await LoadAsync();

        if (Conversations.FirstOrDefault(c => c.Id == conversationId) is { } conversation)
            Selected = conversation;
    }

    partial void OnSelectedChanged(ConversationViewModel? oldValue, ConversationViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        IsMembersPanelOpen = false;
        if (newValue is null)
        {
            Thread = null;
            return;
        }

        IsStartingChat = false;
        var thread = new MessageThreadViewModel(_context, newValue.Id, null, newValue.Title, newValue.Subtitle);
        Thread = thread;
        _ = OpenAsync(newValue, thread);
    }

    private async Task OpenAsync(ConversationViewModel conversation, MessageThreadViewModel thread)
    {
        await thread.LoadAsync();
        await MarkReadAsync(conversation);
    }

    private async Task MarkReadAsync(ConversationViewModel conversation)
    {
        conversation.UnreadCount = 0;
        RaiseUnreadChanged();
        try
        {
            await _context.Api.MarkConversationReadAsync(conversation.Id);
        }
        catch (Exception)
        {
            // Not worth interrupting the user: the next successful open marks it read.
        }
    }

    [RelayCommand]
    private void ToggleMembersPanel()
    {
        IsMembersPanelOpen = !IsMembersPanelOpen;
        if (IsMembersPanelOpen)
        {
            MemberEmailQuery = string.Empty;
            MemberManageStatus = null;
            RefreshMemberSuggestions();
        }
        else
        {
            MemberSuggestions.Clear();
            MemberManageStatus = null;
        }
    }

    partial void OnMemberEmailQueryChanged(string value) => RefreshMemberSuggestions();

    [RelayCommand]
    private Task PickMemberSuggestionAsync(PersonViewModel person)
    {
        MemberEmailQuery = person.Email;
        return AddMemberAsync();
    }

    [RelayCommand]
    private async Task AddMemberAsync()
    {
        if (Selected is not { IsGroup: true } conversation) return;
        var match = ResolveMemberInvitee();
        if (match is null)
        {
            MemberManageStatus = string.IsNullOrWhiteSpace(MemberEmailQuery)
                ? "Enter an email to add someone."
                : "No matching person in this organization.";
            return;
        }

        MemberManageStatus = null;
        try
        {
            var updated = await _context.Api.AddConversationMemberAsync(conversation.Id, match.Id);
            conversation.Update(updated);
            MemberEmailQuery = string.Empty;
            RefreshMemberSuggestions();
        }
        catch (Exception ex)
        {
            MemberManageStatus = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(PersonViewModel person)
    {
        if (Selected is not { IsGroup: true } conversation || person.IsMe) return;
        MemberManageStatus = null;
        try
        {
            var updated = await _context.Api.RemoveConversationMemberAsync(conversation.Id, person.Id);
            conversation.Update(updated);
            RefreshMemberSuggestions();
        }
        catch (Exception ex)
        {
            MemberManageStatus = Describe.Error(ex);
        }
    }

    private PersonViewModel? ResolveMemberInvitee()
    {
        var q = MemberEmailQuery.Trim();
        if (q.Length == 0) return null;
        var eligible = EligibleMemberInvitees().ToList();
        return eligible.FirstOrDefault(p => p.Email.Equals(q, StringComparison.OrdinalIgnoreCase))
               ?? (MemberSuggestions.Count == 1 ? MemberSuggestions[0] : null);
    }

    private IEnumerable<PersonViewModel> EligibleMemberInvitees()
    {
        if (Selected is not { IsGroup: true } conversation)
            return [];
        var already = conversation.Members.Select(m => m.Id).ToHashSet();
        return _context.People.People.Where(p =>
            !p.IsMe
            && !already.Contains(p.Id)
            && !string.IsNullOrWhiteSpace(p.Email));
    }

    private void RefreshMemberSuggestions()
    {
        MemberSuggestions.Clear();
        if (!CanManageMembers)
        {
            OnPropertyChanged(nameof(HasMemberSuggestions));
            return;
        }

        var q = MemberEmailQuery.Trim();
        if (q.Length == 0)
        {
            OnPropertyChanged(nameof(HasMemberSuggestions));
            return;
        }

        foreach (var person in EligibleMemberInvitees()
                     .Where(p => p.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(8))
        {
            MemberSuggestions.Add(person);
        }

        OnPropertyChanged(nameof(HasMemberSuggestions));
    }

    [RelayCommand]
    private async Task StartCallAsync()
    {
        if (Selected is null || StartCallHandler is null) return;
        ErrorMessage = null;
        try
        {
            await StartCallHandler(Selected.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private void BeginNewChat()
    {
        Candidates.Clear();
        foreach (var person in _context.People.People.Where(p => !p.IsMe))
        {
            person.IsSelected = false;
            Candidates.Add(person);
        }

        NewChatTitle = string.Empty;
        NewChatError = null;
        Selected = null;
        IsStartingChat = true;
    }

    [RelayCommand]
    private void CancelNewChat() => IsStartingChat = false;

    [RelayCommand]
    private async Task StartChatAsync()
    {
        var chosen = Candidates.Where(p => p.IsSelected).Select(p => p.Id).ToList();
        if (chosen.Count == 0)
        {
            NewChatError = "Choose at least one person.";
            return;
        }

        NewChatError = null;
        try
        {
            var created = await _context.Api.CreateConversationAsync(
                _context.Organization.Id,
                new CreateConversationRequest(chosen, chosen.Count > 1 ? NewChatTitle : null));

            IsStartingChat = false;
            await LoadAsync();
            Selected = Conversations.FirstOrDefault(c => c.Id == created.Id);
        }
        catch (Exception ex)
        {
            NewChatError = Describe.Error(ex);
        }
    }

    private void OnConversationsChanged() => _ = LoadAsync();

    private void OnTyping(TypingDto typing) => Thread?.ApplyTyping(typing);

    private void OnMessageReceived(MessageDto message)
    {
        if (message.ConversationId is not { } conversationId) return;

        var conversation = Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (conversation is null)
        {
            _ = LoadAsync(); // A conversation this client has not seen yet.
            return;
        }

        var isOpen = ReferenceEquals(conversation, Selected);
        var fromMe = message.Sender.Id == _context.People.MyId;
        if (message.ParentMessageId is null)
        {
            conversation.RecordMessage(message, countAsUnread: !isOpen && !fromMe);
            var index = Conversations.IndexOf(conversation);
            if (index > 0) Conversations.Move(index, 0);
        }

        if (isOpen)
        {
            Thread?.Append(message);
            if (!fromMe && message.ParentMessageId is null) _ = MarkReadAsync(conversation);
        }

        RaiseUnreadChanged();
    }

    private void OnMessageUpdated(MessageDto message)
    {
        if (message.ConversationId is not { } conversationId) return;

        var conversation = Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (conversation is not null && message.ParentMessageId is null
            && conversation.LastMessageAt == message.CreatedAt)
        {
            conversation.RecordMessage(message, countAsUnread: false);
        }

        if (ReferenceEquals(conversation, Selected)) Thread?.ApplyUpdate(message);
    }

    private void RaiseUnreadChanged()
    {
        OnPropertyChanged(nameof(UnreadTotal));
        UnreadChanged?.Invoke();
    }
}
