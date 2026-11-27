using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Services;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public partial class RailItemViewModel(string label, string icon, ViewModelBase section) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>Key of the icon geometry resource.</summary>
    public string Icon { get; } = icon;

    public ViewModelBase Section { get; } = section;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private int _badge;

    public bool HasBadge => Badge > 0;
}

/// <summary>The signed-in app: navigation rail plus the current section.</summary>
public partial class ShellViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private MeetingReminderWatcher? _meetingReminders;

    public ShellViewModel(
        SessionContext context,
        MeDto me,
        AppConfig config,
        Func<Task> signOut,
        Func<Task> onLeftOrganization,
        IAppUpdateService? updates = null)
    {
        _context = context;

        Activity = new ActivityViewModel(context);
        Chat = new ChatViewModel(context);
        Workspaces = new WorkspacesViewModel(context);
        Calendar = new CalendarViewModel(context);
        Files = new FilesViewModel(context);
        Music = new MusicViewModel(context);
        Settings = new SettingsViewModel(context, me, config, signOut, onLeftOrganization, updates);

        Calls = new CallsViewModel(context);

        _activityItem = new RailItemViewModel("Activity", "Icon.Activity", Activity);
        _chatItem = new RailItemViewModel("Chat", "Icon.Chat", Chat);
        _calendarItem = new RailItemViewModel("Calendar", "Icon.Calendar", Calendar);
        _callsItem = new RailItemViewModel("Calls", "Icon.Calls", Calls);
        _filesItem = new RailItemViewModel("Files", "Icon.Files", Files);
        _musicItem = new RailItemViewModel("Music", "Icon.Music", Music);
        RailItems =
        [
            _activityItem,
            _chatItem,
            new RailItemViewModel("Workspaces", "Icon.Workspaces", Workspaces),
            _calendarItem,
            _callsItem,
            _filesItem,
            _musicItem,
        ];
        SettingsItem = new RailItemViewModel("Settings", "Icon.Settings", Settings);

        _current = Chat;
        _chatItem.IsSelected = true;
        _isConnected = true;

        Activity.UnreadChanged += () => _activityItem.Badge = Activity.UnreadCount;
        Activity.IncomingMeetingInvite += OnIncomingMeetingInvite;
        Activity.IncomingMention += OnIncomingMention;
        Activity.ConversationLookup = id => Chat.Conversations.FirstOrDefault(c => c.Id == id);
        Activity.ChannelLookup = id =>
        {
            var channel = Workspaces.Workspaces.SelectMany(w => w.Channels).FirstOrDefault(c => c.Id == id);
            return channel is null
                ? null
                : (channel.Name, $"{channel.Workspace.Name} · {channel.Workspace.MemberCount} members");
        };
        Activity.OpenCalendarHandler = () => ShowCalendar();
        Activity.OpenCallsHandler = () => OpenCalls();
        Activity.JoinMeetingHandler = async meetingId =>
        {
            Navigate(_calendarItem);
            await Calendar.JoinFromInviteAsync(meetingId);
        };
        Chat.UnreadChanged += () => _chatItem.Badge = Chat.UnreadTotal;
        Calls.IncomingChanged += () =>
        {
            _callsItem.Badge = Calls.HasIncoming ? 1 : 0;
            OnPropertyChanged(nameof(ShowIncomingCall));
        };
        Calls.CallDeclined += OnCallDeclined;
        Calls.CallChromeChanged += OnCallChromeChanged;
        Chat.StartCallHandler = async conversationId =>
        {
            var peer = Chat.Selected?.Title;
            await Calls.StartFromConversationAsync(conversationId, peer);
        };
        Calls.OpenConversationHandler = async conversationId =>
        {
            Navigate(_chatItem);
            await Chat.OpenConversationAsync(conversationId);
        };
        Music.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MusicViewModel.HasNowPlaying)
                or nameof(MusicViewModel.NowPlayingLine)
                or nameof(MusicViewModel.IsPlaying)
                or nameof(MusicViewModel.NowPlayingTitle)
                or nameof(MusicViewModel.NowPlayingArtist))
            {
                OnPropertyChanged(nameof(ShowMusicMiniPlayer));
                OnPropertyChanged(nameof(MusicMiniPlayerLine));
            }
        };
        Calendar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CalendarViewModel.IsInMeeting)
                or nameof(CalendarViewModel.Stage))
            {
                OnPropertyChanged(nameof(ShowMeetingBanner));
                OnPropertyChanged(nameof(ShowMeetingPip));
                if (Calendar.IsInMeeting) _ = Music.PauseForCallAsync();
            }
        };

        context.Realtime.PresenceChanged += OnPresenceChanged;
        context.Realtime.MembersChanged += OnMembersChanged;
        context.Realtime.ConnectionChanged += OnConnectionChanged;
    }

    private readonly RailItemViewModel _activityItem;
    private readonly RailItemViewModel _chatItem;
    private readonly RailItemViewModel _calendarItem;
    private readonly RailItemViewModel _callsItem;
    private readonly RailItemViewModel _filesItem;
    private readonly RailItemViewModel _musicItem;

    public IReadOnlyList<RailItemViewModel> RailItems { get; }
    public RailItemViewModel SettingsItem { get; }

    public ActivityViewModel Activity { get; }
    public ChatViewModel Chat { get; }
    public WorkspacesViewModel Workspaces { get; }
    public CalendarViewModel Calendar { get; }
    public CallsViewModel Calls { get; }
    public FilesViewModel Files { get; }
    public MusicViewModel Music { get; }
    public SettingsViewModel Settings { get; }

    public PersonViewModel Me => _context.People.Me;
    public string OrganizationName => _context.Organization.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeetingBanner), nameof(ShowMeetingPip), nameof(ShowCallStage), nameof(ShowCallBar), nameof(ShowMusicMiniPlayer))]
    private ViewModelBase _current;

    [ObservableProperty] private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeetingInviteToast))]
    private MeetingInvitePrompt? _pendingMeetingInvite;

    /// <summary>Latest mention waiting for OS notification click / app activation → open chat.</summary>
    private MentionPrompt? _pendingOsMention;
    private DateTimeOffset _pendingOsMentionAt;

    /// <summary>Bottom-right toast when someone invites the user into a meeting.</summary>
    public bool ShowMeetingInviteToast => PendingMeetingInvite is not null;

    /// <summary>Text banner only when the meeting PiP cannot be shown yet.</summary>
    public bool ShowMeetingBanner =>
        Calendar.IsInMeeting && !ReferenceEquals(Current, Calendar) && !ShowMeetingPip;

    /// <summary>Teams-style floating PiP while browsing other sections during a live meeting.</summary>
    public bool ShowMeetingPip =>
        Calendar.IsInMeeting
        && Calendar.Stage is { IsMediaOpen: true }
        && !ReferenceEquals(Current, Calendar);

    /// <summary>Incoming ring overlay — visible from any section.</summary>
    public bool ShowIncomingCall => Calls.HasIncoming;

    /// <summary>Full in-app LiveKit stage (Teams-style immersive call).</summary>
    public bool ShowCallStage => Calls.HasMedia && Calls.IsStageExpanded;

    /// <summary>
    /// Compact bar for recovered calls without LiveKit. When media is up, the floating PiP
    /// replaces this bar while browsing.
    /// </summary>
    public bool ShowCallBar => Calls.IsInCall && !ShowCallStage && !Calls.HasMedia;

    /// <summary>Compact Spotify now-playing while browsing other rail sections.</summary>
    public bool ShowMusicMiniPlayer =>
        Music.HasNowPlaying && !ReferenceEquals(Current, Music);

    public string MusicMiniPlayerLine => Music.NowPlayingLine;

    public async Task InitializeAsync()
    {
        await LoadPeopleAsync();

        // Lists first, so the app is usable even if the realtime connection is slow to come up.
        await Task.WhenAll(
            Activity.LoadAsync(), Chat.LoadAsync(), Workspaces.LoadAsync(), Calendar.LoadAsync(), Calls.LoadAsync());
        await _context.Realtime.StartAsync();
        IsConnected = _context.Realtime.IsConnected;

        // Native OS banners ~10 minutes before scheduled meetings (host + invitees with MeetJoe open).
        _meetingReminders = new MeetingReminderWatcher(
            _context.Api,
            _context.Notifications,
            () => _context.Organization.Id,
            () => _context.People.MyId);
        _meetingReminders.Start();
    }

    public async Task ShutdownAsync()
    {
        _meetingReminders?.Dispose();
        _meetingReminders = null;
        _context.Realtime.PresenceChanged -= OnPresenceChanged;
        _context.Realtime.MembersChanged -= OnMembersChanged;
        _context.Realtime.ConnectionChanged -= OnConnectionChanged;
        Activity.Detach();
        Chat.Detach();
        Workspaces.Detach();
        Calendar.Detach();
        Calls.Detach();
        Music.Detach();
        await _context.Realtime.StopAsync();
    }

    [RelayCommand]
    private void Navigate(RailItemViewModel item)
    {
        // Leaving the immersive stage keeps the LiveKit session alive in a floating bar.
        if (Calls.HasMedia && Calls.IsStageExpanded && !ReferenceEquals(item.Section, Calls))
            Calls.MinimizeStageCommand.Execute(null);

        foreach (var other in RailItems) other.IsSelected = ReferenceEquals(other, item);
        SettingsItem.IsSelected = ReferenceEquals(SettingsItem, item);
        Current = item.Section;

        if (ReferenceEquals(item, _activityItem)) _ = Activity.LoadAsync();
        if (ReferenceEquals(item, _callsItem))
        {
            _ = Calls.LoadAsync();
            if (Calls.HasMedia) Calls.ExpandStageCommand.Execute(null);
        }
        if (ReferenceEquals(item, _filesItem)) _ = Files.LoadAsync();
        if (ReferenceEquals(item, _musicItem)) _ = Music.LoadAsync();
    }

    public void OpenCalls() => Navigate(_callsItem);

    [RelayCommand]
    private void OpenMusic() => Navigate(_musicItem);

    [RelayCommand]
    private void ReturnToMeeting() => Navigate(_calendarItem);

    public void ShowCalendar() => Navigate(_calendarItem);

    [RelayCommand]
    private void ReturnToCall() => Calls.ExpandStageCommand.Execute(null);

    [RelayCommand]
    private void DismissMeetingInvite()
    {
        var prompt = PendingMeetingInvite;
        PendingMeetingInvite = null;
        if (prompt is null) return;
        var item = Activity.Items.FirstOrDefault(i => i.Id == prompt.ActivityId);
        if (item is not null && item.IsUnread && Activity.MarkReadCommand.CanExecute(item))
            Activity.MarkReadCommand.Execute(item);
    }

    [RelayCommand]
    private async Task JoinMeetingInviteAsync()
    {
        var prompt = PendingMeetingInvite;
        if (prompt is null) return;

        PendingMeetingInvite = null;
        var item = Activity.Items.FirstOrDefault(i => i.Id == prompt.ActivityId);
        if (item is not null && item.IsUnread && Activity.MarkReadCommand.CanExecute(item))
            Activity.MarkReadCommand.Execute(item);

        Navigate(_calendarItem);
        await Calendar.JoinFromInviteAsync(prompt.MeetingId);
    }

    private void OnIncomingMeetingInvite(MeetingInvitePrompt prompt)
    {
        // Newest invite wins; keep a single toast so the corner stays uncluttered.
        PendingMeetingInvite = prompt;
    }

    private void OnIncomingMention(MentionPrompt prompt)
    {
        if (prompt.ConversationId is { } conversationId)
            Chat.ApplyIncomingMention(conversationId);

        _pendingOsMention = prompt;
        _pendingOsMentionAt = DateTimeOffset.UtcNow;
        var action = prompt.ConversationId is { } id
            ? new Uri($"meetjoe://chat?id={id:D}")
            : null;
        // Native MeetJoe Notification Center / Windows toast — Show opens this group chat.
        _context.Notifications.Show(
            "Mentioned",
            prompt.Summary,
            action,
            "Show");
    }

    /// <summary>Opens a group chat from meetjoe://chat?id=… (notification Show / activation).</summary>
    public Task OpenChatFromDeepLinkAsync(Guid conversationId) =>
        OpenConversationFromMentionAsync(conversationId, markActivityId: null);

    /// <summary>
    /// When the user focuses MeetJoe after a mention OS alert (macOS click / dock),
    /// open the pending group chat once.
    /// </summary>
    public Task TryOpenPendingMentionAsync()
    {
        var prompt = _pendingOsMention;
        if (prompt?.ConversationId is not { } conversationId) return Task.CompletedTask;
        // Ignore stale alerts so ordinary app focus does not jump chats long after the banner.
        if (DateTimeOffset.UtcNow - _pendingOsMentionAt > TimeSpan.FromMinutes(2))
        {
            _pendingOsMention = null;
            return Task.CompletedTask;
        }

        _pendingOsMention = null;
        return OpenConversationFromMentionAsync(conversationId, prompt.ActivityId);
    }

    private async Task OpenConversationFromMentionAsync(Guid conversationId, Guid? markActivityId)
    {
        if (markActivityId is { } activityId)
        {
            var item = Activity.Items.FirstOrDefault(i => i.Id == activityId);
            if (item is not null && item.IsUnread && Activity.MarkReadCommand.CanExecute(item))
                Activity.MarkReadCommand.Execute(item);
        }

        _pendingOsMention = null;
        Navigate(_chatItem);
        await Chat.OpenConversationAsync(conversationId);
    }

    private void OnCallDeclined(CallDeclinedNotice notice)
    {
        // Native macOS / Windows banner — not an in-app toast.
        _context.Notifications.Show(
            "Call declined",
            $"{notice.Message} They declined the incoming call.");
    }

    private void OnCallChromeChanged()
    {
        OnPropertyChanged(nameof(ShowIncomingCall));
        OnPropertyChanged(nameof(ShowCallStage));
        OnPropertyChanged(nameof(ShowCallBar));
        if (Calls.HasIncoming || Calls.IsInCall || Calls.HasMedia)
            _ = Music.PauseForCallAsync();
    }

    private async Task LoadPeopleAsync()
    {
        try
        {
            _context.People.Apply(await _context.Api.GetMembersAsync(_context.Organization.Id));
        }
        catch (Exception)
        {
            // The sections report their own load failures; names fill in on the next refresh.
        }
    }

    private void OnPresenceChanged(PresenceDto presence) => _context.People.Apply(presence);

    private void OnMembersChanged() => _ = LoadPeopleAsync();

    private void OnConnectionChanged(bool connected)
    {
        var wasConnected = IsConnected;
        IsConnected = connected;
        if (connected && !wasConnected)
        {
            // Events sent while disconnected are gone; fetch current state instead.
            _ = LoadPeopleAsync();
            _ = Activity.LoadAsync();
            _ = Chat.LoadAsync();
            _ = Workspaces.LoadAsync();
            _ = Calendar.LoadAsync();
            _ = Calls.LoadAsync();
        }
    }
}
