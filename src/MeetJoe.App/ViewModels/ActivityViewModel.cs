using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public partial class ActivityItemViewModel(ActivityDto item, PeopleStore people) : ObservableObject
{
    public Guid Id { get; } = item.Id;
    public Guid? MessageId { get; } = item.MessageId;
    public Guid? ConversationId { get; } = item.ConversationId;
    public Guid? ChannelId { get; } = item.ChannelId;
    public Guid? MeetingId { get; } = item.MeetingId;
    public Guid? CallId { get; } = item.CallId;
    public ActivityKind Kind { get; } = item.Kind;
    public PersonViewModel Actor { get; } = people.Get(item.Actor);
    public string Summary { get; } = item.Summary;
    public string TimeText { get; } = Describe.Time(item.CreatedAt);
    public string KindLabel { get; } = item.Kind switch
    {
        ActivityKind.Reply => "Reply",
        ActivityKind.Reaction => "Reaction",
        ActivityKind.Mention => "Mention",
        ActivityKind.MeetingInvite => "Meeting invite",
        ActivityKind.MissedCall => "Missed call",
        _ => item.Kind.ToString(),
    };

    /// <summary>Teams-style short line above the summary snippet.</summary>
    public string Headline { get; } = item.Kind switch
    {
        ActivityKind.Reply => $"{people.Get(item.Actor).DisplayName} replied to you",
        ActivityKind.Reaction => $"{people.Get(item.Actor).DisplayName} reacted to your message",
        ActivityKind.Mention => $"{people.Get(item.Actor).DisplayName} mentioned you",
        ActivityKind.MeetingInvite => $"{people.Get(item.Actor).DisplayName} invited you to a meeting",
        ActivityKind.MissedCall => $"Missed call from {people.Get(item.Actor).DisplayName}",
        _ => item.Summary,
    };

    public string KindIconKey { get; } = item.Kind switch
    {
        ActivityKind.Reply => "Icon.Reply",
        ActivityKind.Reaction => "Icon.Smile",
        ActivityKind.Mention => "Icon.Chat",
        ActivityKind.MeetingInvite => "Icon.Video",
        ActivityKind.MissedCall => "Icon.Calls",
        _ => "Icon.Activity",
    };

    /// <summary>Hide the body when the headline already carries the whole story (e.g. missed call).</summary>
    public bool ShowSummaryDetail =>
        Kind is not ActivityKind.MissedCall
        && !string.Equals(Summary, Headline, StringComparison.OrdinalIgnoreCase);

    public bool CanOpenChat => ConversationId is not null || ChannelId is not null;

    [ObservableProperty] private bool _isUnread = item.IsUnread;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Payload for a shell toast when someone invites the user to a meeting.</summary>
public sealed record MeetingInvitePrompt(
    Guid ActivityId,
    Guid MeetingId,
    string Summary,
    string ActorName,
    string TimeText);

/// <summary>Payload for a shell toast when the user is @mentioned in a group chat.</summary>
public sealed record MentionPrompt(
    Guid ActivityId,
    Guid? ConversationId,
    string Summary,
    string ActorName,
    string TimeText);

/// <summary>Mentions, replies, and reactions aimed at the signed-in user.</summary>
public partial class ActivityViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private readonly HashSet<Guid> _knownMeetingInviteIds = [];
    private readonly HashSet<Guid> _knownMentionIds = [];
    private bool _meetingInvitesSeeded;
    private bool _mentionsSeeded;
    private int _openGeneration;

    public ActivityViewModel(SessionContext context)
    {
        _context = context;
        context.Realtime.ActivityChanged += OnActivityChanged;
    }

    public ObservableCollection<ActivityItemViewModel> Items { get; } = [];

    /// <summary>Optional: resolve conversation chrome from the Chat list when already loaded.</summary>
    public Func<Guid, ConversationViewModel?>? ConversationLookup { get; set; }

    /// <summary>Optional: resolve channel chrome from Workspaces when already loaded.</summary>
    public Func<Guid, (string Name, string Subtitle)?>? ChannelLookup { get; set; }

    public Action? OpenCalendarHandler { get; set; }
    public Action? OpenCallsHandler { get; set; }
    public Func<Guid, Task>? JoinMeetingHandler { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowEmptyDetail), nameof(ShowThread), nameof(ShowDetailCard))]
    private ActivityItemViewModel? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThread), nameof(ShowDetailCard), nameof(ShowEmptyDetail))]
    private MessageThreadViewModel? _thread;

    [ObservableProperty] private string _detailTitle = string.Empty;
    [ObservableProperty] private string _detailSubtitle = string.Empty;

    public bool IsEmpty => !IsLoading && Items.Count == 0 && ErrorMessage is null;
    public bool HasUnread => UnreadCount > 0;
    public bool HasSelection => Selected is not null;
    public bool ShowThread => Thread is not null;
    public bool ShowDetailCard => Selected is not null && Thread is null && !CanOpenSelectedAsChat;
    public bool ShowEmptyDetail => Selected is null;
    private bool CanOpenSelectedAsChat => Selected?.CanOpenChat == true;

    public event Action? UnreadChanged;

    /// <summary>Fired for newly arrived unread meeting invites (not historical ones from first load).</summary>
    public event Action<MeetingInvitePrompt>? IncomingMeetingInvite;

    /// <summary>Fired for newly arrived unread @mentions in group chat.</summary>
    public event Action<MentionPrompt>? IncomingMention;

    public void Detach() => _context.Realtime.ActivityChanged -= OnActivityChanged;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        var selectedId = Selected?.Id;
        try
        {
            var feed = await _context.Api.GetActivityAsync(_context.Organization.Id);
            Items.Clear();
            foreach (var item in feed) Items.Add(new ActivityItemViewModel(item, _context.People));
            UnreadCount = Items.Count(i => i.IsUnread);
            UnreadChanged?.Invoke();
            DetectNewMeetingInvites();
            DetectNewMentions();

            if (selectedId is { } id && Items.FirstOrDefault(i => i.Id == id) is { } match)
            {
                match.IsSelected = true;
                Selected = match;
            }
            else
            {
                Selected = null;
                Thread = null;
                DetailTitle = string.Empty;
                DetailSubtitle = string.Empty;
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
        }
    }

    private void DetectNewMeetingInvites()
    {
        var invites = Items
            .Where(i => i.Kind == ActivityKind.MeetingInvite && i.MeetingId is not null)
            .ToList();

        if (!_meetingInvitesSeeded)
        {
            foreach (var item in invites) _knownMeetingInviteIds.Add(item.Id);
            _meetingInvitesSeeded = true;
            return;
        }

        foreach (var item in invites.Where(i => i.IsUnread))
        {
            if (!_knownMeetingInviteIds.Add(item.Id)) continue;
            IncomingMeetingInvite?.Invoke(new MeetingInvitePrompt(
                item.Id,
                item.MeetingId!.Value,
                item.Summary,
                item.Actor.DisplayName,
                item.TimeText));
        }
    }

    private void DetectNewMentions()
    {
        var mentions = Items
            .Where(i => i.Kind == ActivityKind.Mention && i.ConversationId is not null)
            .ToList();

        if (!_mentionsSeeded)
        {
            foreach (var item in mentions) _knownMentionIds.Add(item.Id);
            _mentionsSeeded = true;
            return;
        }

        foreach (var item in mentions.Where(i => i.IsUnread))
        {
            if (!_knownMentionIds.Add(item.Id)) continue;
            IncomingMention?.Invoke(new MentionPrompt(
                item.Id,
                item.ConversationId,
                item.Summary,
                item.Actor.DisplayName,
                item.TimeText));
        }
    }

    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        try
        {
            await _context.Api.MarkAllActivityReadAsync(_context.Organization.Id);
            foreach (var item in Items) item.IsUnread = false;
            UnreadCount = 0;
            UnreadChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task MarkReadAsync(ActivityItemViewModel item)
    {
        if (!item.IsUnread) return;
        try
        {
            await _context.Api.MarkActivityReadAsync(item.Id);
            item.IsUnread = false;
            UnreadCount = Items.Count(i => i.IsUnread);
            UnreadChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task SelectAsync(ActivityItemViewModel item)
    {
        if (Selected is not null) Selected.IsSelected = false;
        item.IsSelected = true;
        Selected = item;
        DetailTitle = item.Headline;
        DetailSubtitle = item.TimeText;

        if (item.IsUnread)
            await MarkReadAsync(item);

        var generation = ++_openGeneration;
        Thread = null;

        if (item.ConversationId is { } conversationId)
        {
            await OpenConversationAsync(item, conversationId, generation);
            return;
        }

        if (item.ChannelId is { } channelId)
        {
            await OpenChannelAsync(item, channelId, generation);
            return;
        }

        // Meeting / missed call / other — detail card only.
        OnPropertyChanged(nameof(ShowDetailCard));
        OnPropertyChanged(nameof(ShowEmptyDetail));
        OnPropertyChanged(nameof(ShowThread));
    }

    private async Task OpenConversationAsync(ActivityItemViewModel item, Guid conversationId, int generation)
    {
        var known = ConversationLookup?.Invoke(conversationId);
        var title = known?.Title ?? item.Actor.DisplayName;
        var subtitle = known?.Subtitle ?? item.KindLabel;
        var thread = new MessageThreadViewModel(_context, conversationId, null, title, subtitle,
            known is { IsGroup: true } ? known.Members.Where(m => !m.IsMe).ToList() : null);

        if (generation != _openGeneration) return;
        DetailTitle = title;
        DetailSubtitle = subtitle;
        Thread = thread;

        try
        {
            if (known is not null)
                thread.ConfigureSeenBy(known.Members, known.ReadCursors.Values);
            await thread.LoadAsync();
            if (generation != _openGeneration) return;
            await _context.Api.MarkConversationReadAsync(conversationId);
            thread.ApplyReadReceipt(_context.People.MyId, DateTimeOffset.UtcNow, _context.People.Me.DisplayName);
        }
        catch (Exception ex)
        {
            if (generation == _openGeneration)
                ErrorMessage = Describe.Error(ex);
        }
    }

    private async Task OpenChannelAsync(ActivityItemViewModel item, Guid channelId, int generation)
    {
        var known = ChannelLookup?.Invoke(channelId);
        var title = known?.Name ?? "Channel";
        var subtitle = known?.Subtitle ?? item.KindLabel;
        var thread = new MessageThreadViewModel(_context, null, channelId, title, subtitle);

        if (generation != _openGeneration) return;
        DetailTitle = title;
        DetailSubtitle = subtitle;
        Thread = thread;

        try
        {
            await thread.LoadAsync();
            if (generation != _openGeneration) return;
            await _context.Api.MarkChannelReadAsync(channelId);
            thread.ApplyReadReceipt(_context.People.MyId, DateTimeOffset.UtcNow, _context.People.Me.DisplayName);
        }
        catch (Exception ex)
        {
            if (generation == _openGeneration)
                ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private void OpenCalendar() => OpenCalendarHandler?.Invoke();

    [RelayCommand]
    private void OpenCalls() => OpenCallsHandler?.Invoke();

    [RelayCommand]
    private async Task JoinMeetingAsync()
    {
        if (Selected?.MeetingId is not { } meetingId || JoinMeetingHandler is null) return;
        await JoinMeetingHandler(meetingId);
    }

    public bool CanJoinMeeting => Selected?.Kind == ActivityKind.MeetingInvite && Selected.MeetingId is not null;
    public bool CanOpenCalls => Selected?.Kind == ActivityKind.MissedCall;

    partial void OnSelectedChanged(ActivityItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanJoinMeeting));
        OnPropertyChanged(nameof(CanOpenCalls));
    }

    private void OnActivityChanged() => _ = LoadAsync();
}
