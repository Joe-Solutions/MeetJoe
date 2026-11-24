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

    public ShellViewModel(
        SessionContext context, MeDto me, AppConfig config, Func<Task> signOut, Func<Task> onLeftOrganization)
    {
        _context = context;

        Activity = new ActivityViewModel(context);
        Chat = new ChatViewModel(context);
        Workspaces = new WorkspacesViewModel(context);
        Calendar = new CalendarViewModel(context);
        Files = new FilesViewModel(context);
        Settings = new SettingsViewModel(context, me, config, signOut, onLeftOrganization);

        Calls = new CallsViewModel(context);

        _activityItem = new RailItemViewModel("Activity", "Icon.Activity", Activity);
        _chatItem = new RailItemViewModel("Chat", "Icon.Chat", Chat);
        _calendarItem = new RailItemViewModel("Calendar", "Icon.Calendar", Calendar);
        _callsItem = new RailItemViewModel("Calls", "Icon.Calls", Calls);
        _filesItem = new RailItemViewModel("Files", "Icon.Files", Files);
        RailItems =
        [
            _activityItem,
            _chatItem,
            new RailItemViewModel("Workspaces", "Icon.Workspaces", Workspaces),
            _calendarItem,
            _callsItem,
            _filesItem,
        ];
        SettingsItem = new RailItemViewModel("Settings", "Icon.Settings", Settings);

        _current = Chat;
        _chatItem.IsSelected = true;
        _isConnected = true;

        Activity.UnreadChanged += () => _activityItem.Badge = Activity.UnreadCount;
        Chat.UnreadChanged += () => _chatItem.Badge = Chat.UnreadTotal;
        Calls.IncomingChanged += () =>
        {
            _callsItem.Badge = Calls.HasIncoming ? 1 : 0;
            OnPropertyChanged(nameof(ShowIncomingCall));
        };
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
        Calendar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalendarViewModel.IsInMeeting)) OnPropertyChanged(nameof(ShowMeetingBanner));
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

    public IReadOnlyList<RailItemViewModel> RailItems { get; }
    public RailItemViewModel SettingsItem { get; }

    public ActivityViewModel Activity { get; }
    public ChatViewModel Chat { get; }
    public WorkspacesViewModel Workspaces { get; }
    public CalendarViewModel Calendar { get; }
    public CallsViewModel Calls { get; }
    public FilesViewModel Files { get; }
    public SettingsViewModel Settings { get; }

    public PersonViewModel Me => _context.People.Me;
    public string OrganizationName => _context.Organization.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeetingBanner), nameof(ShowCallStage), nameof(ShowCallBar))]
    private ViewModelBase _current;

    [ObservableProperty] private bool _isConnected;

    /// <summary>A reminder, on every other section, that the user is still in a meeting.</summary>
    public bool ShowMeetingBanner => Calendar.IsInMeeting && !ReferenceEquals(Current, Calendar);

    /// <summary>Incoming ring overlay — visible from any section.</summary>
    public bool ShowIncomingCall => Calls.HasIncoming;

    /// <summary>Full in-app LiveKit stage (Teams-style immersive call).</summary>
    public bool ShowCallStage => Calls.HasMedia && Calls.IsStageExpanded;

    /// <summary>Compact bar while browsing, or Leave chrome for a recovered active call.</summary>
    public bool ShowCallBar => Calls.IsInCall && !ShowCallStage;

    public async Task InitializeAsync()
    {
        await LoadPeopleAsync();

        // Lists first, so the app is usable even if the realtime connection is slow to come up.
        await Task.WhenAll(
            Activity.LoadAsync(), Chat.LoadAsync(), Workspaces.LoadAsync(), Calendar.LoadAsync(), Calls.LoadAsync());
        await _context.Realtime.StartAsync();
        IsConnected = _context.Realtime.IsConnected;
    }

    public async Task ShutdownAsync()
    {
        _context.Realtime.PresenceChanged -= OnPresenceChanged;
        _context.Realtime.MembersChanged -= OnMembersChanged;
        _context.Realtime.ConnectionChanged -= OnConnectionChanged;
        Activity.Detach();
        Chat.Detach();
        Workspaces.Detach();
        Calendar.Detach();
        Calls.Detach();
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
    }

    public void OpenCalls() => Navigate(_callsItem);

    [RelayCommand]
    private void ReturnToMeeting() => Navigate(_calendarItem);

    [RelayCommand]
    private void ReturnToCall() => Calls.ExpandStageCommand.Execute(null);

    private void OnCallChromeChanged()
    {
        OnPropertyChanged(nameof(ShowIncomingCall));
        OnPropertyChanged(nameof(ShowCallStage));
        OnPropertyChanged(nameof(ShowCallBar));
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
