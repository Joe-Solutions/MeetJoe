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

    [ObservableProperty] private bool _isUnread = item.IsUnread;
}

/// <summary>Payload for a shell toast when someone invites the user to a meeting.</summary>
public sealed record MeetingInvitePrompt(
    Guid ActivityId,
    Guid MeetingId,
    string Summary,
    string ActorName,
    string TimeText);

/// <summary>Mentions, replies, and reactions aimed at the signed-in user.</summary>
public partial class ActivityViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private readonly HashSet<Guid> _knownMeetingInviteIds = [];
    private bool _meetingInvitesSeeded;

    public ActivityViewModel(SessionContext context)
    {
        _context = context;
        context.Realtime.ActivityChanged += OnActivityChanged;
    }

    public ObservableCollection<ActivityItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool IsEmpty => !IsLoading && Items.Count == 0 && ErrorMessage is null;
    public bool HasUnread => UnreadCount > 0;

    public event Action? UnreadChanged;

    /// <summary>Fired for newly arrived unread meeting invites (not historical ones from first load).</summary>
    public event Action<MeetingInvitePrompt>? IncomingMeetingInvite;

    public void Detach() => _context.Realtime.ActivityChanged -= OnActivityChanged;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var feed = await _context.Api.GetActivityAsync(_context.Organization.Id);
            Items.Clear();
            foreach (var item in feed) Items.Add(new ActivityItemViewModel(item, _context.People));
            UnreadCount = Items.Count(i => i.IsUnread);
            UnreadChanged?.Invoke();
            DetectNewMeetingInvites();
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

    private void OnActivityChanged() => _ = LoadAsync();
}
