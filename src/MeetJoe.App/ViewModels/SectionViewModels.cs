using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Services;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

/// <summary>A rail destination whose feature is scheduled for a later roadmap phase.</summary>
public sealed class PlaceholderViewModel(string title, string message, string phase) : ViewModelBase
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string Phase { get; } = phase;
}

// ---- Files ------------------------------------------------------------------

public partial class FileViewModel(FileDto file, SessionContext context) : ObservableObject
{
    private readonly Guid _fileId = file.Id;
    private readonly string _contentType = file.ContentType;

    public string FileName { get; } = file.FileName;
    public string SizeText { get; } = Describe.FileSize(file.Size);
    public string Location { get; } = file.Location;
    public string SharedText { get; } = $"{file.Uploader.DisplayName} · {Describe.Time(file.CreatedAt)}";

    [ObservableProperty] private string? _status;

    [RelayCommand]
    private async Task ViewAsync()
    {
        try
        {
            Status = "Opening…";
            await using var buffer = new MemoryStream();
            await context.Api.DownloadFileAsync(_fileId, buffer);
            buffer.Position = 0;
            await context.Platform.ShowFilePreviewAsync(FileName, buffer, _contentType);
            Status = null;
        }
        catch (Exception ex)
        {
            Status = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        try
        {
            await using var destination = await context.Platform.OpenSaveStreamAsync(FileName);
            if (destination is null) return;

            Status = "Downloading…";
            await context.Api.DownloadFileAsync(_fileId, destination);
            Status = "Saved";
        }
        catch (Exception ex)
        {
            Status = Describe.Error(ex);
        }
    }
}

public partial class FilesViewModel(SessionContext context) : ViewModelBase
{
    public ObservableCollection<FileViewModel> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;

    public bool IsEmpty => !IsLoading && Files.Count == 0 && ErrorMessage is null;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var files = await context.Api.GetFilesAsync(context.Organization.Id);
            Files.Clear();
            foreach (var file in files) Files.Add(new FileViewModel(file, context));
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
}

// ---- Meetings ---------------------------------------------------------------

public partial class MeetingViewModel : ObservableObject
{
    public MeetingViewModel(MeetingDto meeting, PeopleStore people)
    {
        Apply(meeting, people);
    }

    public Guid Id { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public PersonViewModel Host { get; private set; } = null!;
    public bool IsHostedByMe { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public bool IsScheduled { get; private set; }
    public bool LobbyEnabled { get; private set; }
    public bool IsActive { get; private set; }
    public bool HasEnded { get; private set; }
    public bool CanJoin { get; private set; }
    public IReadOnlyList<PersonViewModel> Participants { get; private set; } = [];
    public IReadOnlyList<PersonViewModel> Lobby { get; private set; } = [];
    public IReadOnlyList<MeetingInviteDto> Invites { get; private set; } = [];
    public IReadOnlyList<Guid> RaisedHands { get; private set; } = [];
    public string Summary { get; private set; } = string.Empty;
    public string WhenText { get; private set; } = string.Empty;
    public InviteStatus? MyInviteStatus { get; private set; }

    public void Apply(MeetingDto meeting, PeopleStore people)
    {
        Id = meeting.Id;
        Title = meeting.Title;
        Description = meeting.Description;
        Host = people.Get(meeting.Host);
        IsHostedByMe = meeting.Host.Id == people.MyId;
        StartsAt = meeting.StartsAt;
        EndsAt = meeting.EndsAt;
        IsScheduled = meeting.IsScheduled;
        LobbyEnabled = meeting.LobbyEnabled;
        IsActive = meeting.IsActive;
        HasEnded = !meeting.IsActive;
        CanJoin = meeting.IsActive;
        Participants = meeting.Participants.Select(people.Get).ToList();
        Lobby = meeting.Lobby.Select(people.Get).ToList();
        Invites = meeting.Invites;
        RaisedHands = meeting.RaisedHands;
        MyInviteStatus = HasEnded
            ? null
            : meeting.Invites.FirstOrDefault(i => i.UserId == people.MyId)?.Status;
        WhenText = meeting.StartsAt.ToLocalTime().ToString("ddd d MMM · h:mm tt");
        Summary = HasEnded
            ? $"Hosted by {meeting.Host.DisplayName} · ended"
            : meeting.Participants.Count switch
            {
                0 => $"Hosted by {meeting.Host.DisplayName} · nobody has joined yet",
                1 => $"Hosted by {meeting.Host.DisplayName} · 1 person in the meeting",
                var n => $"Hosted by {meeting.Host.DisplayName} · {n} people in the meeting",
            };
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>The stage for the meeting the user has joined.</summary>
public partial class MeetingStageViewModel : ViewModelBase
{
    private readonly SessionContext _context;

    public MeetingStageViewModel(
        MeetingViewModel meeting, MeetingJoinDto join, SessionContext context)
    {
        _meeting = meeting;
        _context = context;
        InLobby = join.InLobby;
        MediaServer = join.ServerUrl;
        RoomName = join.RoomName;
        MediaPageUri = BuildMediaPageUri(context.Config.ApiBaseUri, join);
        context.Realtime.MeetingUpdated += OnMeetingUpdated;
        context.Realtime.MeetingChat += OnMeetingChat;
        context.Realtime.ParticipantRemoved += OnRemoved;
        _ = LoadChatAsync();
    }

    public Guid Id => Meeting.Id;

    [ObservableProperty] private MeetingViewModel _meeting;
    [ObservableProperty] private bool _inLobby;
    [ObservableProperty] private string _chatDraft = string.Empty;
    [ObservableProperty] private bool _handRaised;
    [ObservableProperty] private bool _showAddPeople;
    [ObservableProperty] private string? _addPeopleStatus;
    [ObservableProperty] private bool _isMediaOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInviteSuggestions))]
    private string _inviteQuery = string.Empty;

    /// <summary>Right rail: people roster (Teams/Zoom-style) vs meeting chat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatPanel))]
    private bool _showPeoplePanel = true;

    public bool ShowChatPanel => !ShowPeoplePanel;
    public bool HasInviteSuggestions => InviteSuggestions.Count > 0;

    public ObservableCollection<MessageDto> ChatMessages { get; } = [];
    public ObservableCollection<PersonViewModel> InviteSuggestions { get; } = [];

    partial void OnInviteQueryChanged(string value) => RefreshInviteSuggestions();

    /// <summary>Where the media room lives, shown so a connection can be diagnosed.</summary>
    public string MediaServer { get; }

    public string RoomName { get; }

    /// <summary>
    /// In-app LiveKit media page. Token stays in the URL fragment so it is not sent to the MeetJoe API
    /// when the static page is fetched.
    /// </summary>
    public Uri MediaPageUri { get; }

    public void Detach()
    {
        IsMediaOpen = false;
        _context.Realtime.MeetingUpdated -= OnMeetingUpdated;
        _context.Realtime.MeetingChat -= OnMeetingChat;
        _context.Realtime.ParticipantRemoved -= OnRemoved;
    }

    [RelayCommand]
    private void OpenMedia() => IsMediaOpen = !InLobby;

    [RelayCommand]
    private void CloseMedia() => IsMediaOpen = false;

    [RelayCommand]
    private async Task RaiseHandAsync()
    {
        try
        {
            if (HandRaised) await _context.Realtime.LowerHandAsync(Id);
            else await _context.Realtime.RaiseHandAsync(Id);
            HandRaised = !HandRaised;
        }
        catch
        {
            // Best-effort control.
        }
    }

    [RelayCommand]
    private void ShowPeople() => ShowPeoplePanel = true;

    [RelayCommand]
    private void ShowChat() => ShowPeoplePanel = false;

    [RelayCommand]
    private void BeginAddPeople()
    {
        ShowAddPeople = true;
        AddPeopleStatus = null;
        InviteQuery = string.Empty;
        RefreshInviteSuggestions();
    }

    [RelayCommand]
    private void CancelAddPeople()
    {
        ShowAddPeople = false;
        AddPeopleStatus = null;
        InviteQuery = string.Empty;
        InviteSuggestions.Clear();
    }

    [RelayCommand]
    private Task PickInviteSuggestionAsync(PersonViewModel person)
    {
        InviteQuery = person.Email;
        return InvitePeopleAsync();
    }

    [RelayCommand]
    private async Task InvitePeopleAsync()
    {
        var match = ResolveInvitee();
        if (match is null)
        {
            AddPeopleStatus = string.IsNullOrWhiteSpace(InviteQuery)
                ? "Enter an email to find someone."
                : "No one in your organization matches that email.";
            return;
        }

        try
        {
            var updated = await _context.Api.InviteParticipantsAsync(Id, [match.Id]);
            Meeting.Apply(updated, _context.People);
            InviteQuery = string.Empty;
            ShowAddPeople = false;
            AddPeopleStatus = null;
            RefreshInviteSuggestions();
        }
        catch (Exception ex)
        {
            AddPeopleStatus = Describe.Error(ex);
        }
    }

    private PersonViewModel? ResolveInvitee()
    {
        var q = InviteQuery.Trim();
        if (q.Length == 0) return null;

        var eligible = EligibleInvitees().ToList();
        return eligible.FirstOrDefault(p => p.Email.Equals(q, StringComparison.OrdinalIgnoreCase))
               ?? (InviteSuggestions.Count == 1 ? InviteSuggestions[0] : null);
    }

    private IEnumerable<PersonViewModel> EligibleInvitees()
    {
        var present = Meeting.Participants.Select(p => p.Id)
            .Concat(Meeting.Lobby.Select(p => p.Id))
            .Concat(Meeting.Invites.Select(i => i.UserId))
            .Append(_context.People.MyId)
            .ToHashSet();
        return _context.People.People.Where(p =>
            !p.IsMe
            && !present.Contains(p.Id)
            && !string.IsNullOrWhiteSpace(p.Email));
    }

    private void RefreshInviteSuggestions()
    {
        InviteSuggestions.Clear();
        var q = InviteQuery.Trim();
        if (q.Length == 0)
        {
            OnPropertyChanged(nameof(HasInviteSuggestions));
            return;
        }

        foreach (var person in EligibleInvitees()
                     .Where(p => p.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(8))
            InviteSuggestions.Add(person);

        OnPropertyChanged(nameof(HasInviteSuggestions));
    }

    [RelayCommand]
    private async Task AdmitAsync(PersonViewModel person)
    {
        try { await _context.Realtime.AdmitAsync(Id, person.Id); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task RejectAsync(PersonViewModel person)
    {
        try { await _context.Realtime.RejectAsync(Id, person.Id); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task RemoveAsync(PersonViewModel person)
    {
        try { await _context.Realtime.RemoveParticipantAsync(Id, person.Id); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task MuteAsync(PersonViewModel person)
    {
        try { await _context.Realtime.MuteParticipantAsync(Id, person.Id); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        if (string.IsNullOrWhiteSpace(ChatDraft) || InLobby) return;
        var body = ChatDraft;
        ChatDraft = string.Empty;
        try
        {
            var sent = _context.Realtime.IsConnected
                ? await _context.Realtime.SendMeetingChatAsync(Id, body)
                : await _context.Api.SendMeetingChatAsync(Id, body);
            if (ChatMessages.All(m => m.Id != sent.Id)) ChatMessages.Add(sent);
        }
        catch
        {
            ChatDraft = body;
        }
    }

    private async Task LoadChatAsync()
    {
        try
        {
            var messages = await _context.Api.GetMeetingMessagesAsync(Id);
            ChatMessages.Clear();
            foreach (var message in messages) ChatMessages.Add(message);
        }
        catch
        {
            // Chat is optional on first join.
        }
    }

    private void OnMeetingUpdated(MeetingDto meeting)
    {
        if (meeting.Id != Id) return;
        Meeting.Apply(meeting, _context.People);
        HandRaised = meeting.RaisedHands.Contains(_context.People.MyId);
        var wasInLobby = InLobby;
        InLobby = meeting.Lobby.Any(p => p.Id == _context.People.MyId);
        if (wasInLobby && !InLobby) IsMediaOpen = true;
        if (InLobby) IsMediaOpen = false;
        if (ShowAddPeople) RefreshInviteSuggestions();
    }

    private void OnMeetingChat(MessageDto message)
    {
        if (message.MeetingId != Id) return;
        if (ChatMessages.All(m => m.Id != message.Id)) ChatMessages.Add(message);
    }

    private void OnRemoved(Guid meetingId, Guid userId)
    {
        if (meetingId != Id || userId != _context.People.MyId) return;
        // Parent calendar will clear the stage on MeetingsChanged / leave.
    }

    internal static Uri BuildMediaPageUri(Uri apiBase, MeetingJoinDto join)
    {
        // Query string (not hash): some embedded WebViews drop the URL fragment on navigate.
        var page = new Uri(apiBase, "meet/");
        var query = "u=" + Uri.EscapeDataString(join.ServerUrl) + "&t=" + Uri.EscapeDataString(join.Token);
        return new UriBuilder(page) { Query = query }.Uri;
    }
}

/// <summary>One day cell in the schedule month grid.</summary>
public partial class CalendarDayCellViewModel : ObservableObject
{
    public CalendarDayCellViewModel(DateTime date, bool isCurrentMonth, bool hasMeetings, bool isSelected)
    {
        Date = date.Date;
        IsCurrentMonth = isCurrentMonth;
        HasMeetings = hasMeetings;
        IsSelected = isSelected;
        IsToday = Date == DateTime.Today;
    }

    public DateTime Date { get; }
    public int DayNumber => Date.Day;
    public bool IsCurrentMonth { get; }
    public bool IsOtherMonth => !IsCurrentMonth;
    public bool IsToday { get; }

    [ObservableProperty] private bool _hasMeetings;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Calendar destination: meet now, schedule, upcoming list, and the live stage.</summary>
public partial class CalendarViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private readonly List<MeetingViewModel> _scheduledMeetings = [];

    public CalendarViewModel(SessionContext context)
    {
        _context = context;
        SelectedDay = new DateTimeOffset(DateTime.Today);
        var today = DateTime.Today;
        DisplayMonth = new DateTime(today.Year, today.Month, 1);
        context.Realtime.MeetingsChanged += OnMeetingsChanged;
        context.Realtime.MeetingUpdated += OnMeetingUpdated;
        RebuildMonthGrid();
        RefreshSelectedDayMeetings();
    }

    public ObservableCollection<MeetingViewModel> Meetings { get; } = [];
    public ObservableCollection<MeetingViewModel> Upcoming { get; } = [];
    public ObservableCollection<PersonViewModel> InviteCandidates { get; } = [];
    public ObservableCollection<CalendarDayCellViewModel> MonthDays { get; } = [];
    public ObservableCollection<MeetingViewModel> SelectedDayMeetings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _newMeetingTitle = string.Empty;
    [ObservableProperty] private string _scheduleTitle = string.Empty;
    [ObservableProperty] private string _scheduleDescription = string.Empty;
    [ObservableProperty] private string _scheduleStartsAtText = DateTimeOffset.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm");
    [ObservableProperty] private bool _scheduleLobbyEnabled;
    [ObservableProperty] private DateTimeOffset _selectedDay;
    [ObservableProperty] private string _selectedDayLabel = string.Empty;

    /// <summary>Date shown/selected on the schedule calendar (local calendar day).</summary>
    [ObservableProperty] private DateTime? _scheduleSelectedDate = DateTime.Today;

    /// <summary>First day of the month currently shown in the date grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthLabel))]
    private DateTime _displayMonth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInMeeting))]
    private MeetingStageViewModel? _stage;

    public bool IsEmpty => !IsLoading && Meetings.Count == 0 && Upcoming.Count == 0;
    public bool IsInMeeting => Stage is not null;
    public bool HasSelectedDayMeetings => SelectedDayMeetings.Count > 0;
    public string MonthLabel => DisplayMonth.ToString("MMMM yyyy");
    public string SelectedDayMeetingsHeading =>
        ScheduleSelectedDate is { } day
            ? $"On {day:ddd d MMM}"
            : "Scheduled on this day";

    public void Detach()
    {
        _context.Realtime.MeetingsChanged -= OnMeetingsChanged;
        _context.Realtime.MeetingUpdated -= OnMeetingUpdated;
        Stage?.Detach();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            SelectedDayLabel = SelectedDay.ToLocalTime().ToString("dddd, d MMMM");
            var dayStart = new DateTimeOffset(SelectedDay.Date, SelectedDay.Offset);
            var dayEnd = dayStart.AddDays(1);

            // Wide enough window so month cells can show scheduled-meeting highlights.
            var monthAnchor = DisplayMonth == default
                ? new DateTime(dayStart.Year, dayStart.Month, 1)
                : DisplayMonth;
            var from = new DateTimeOffset(monthAnchor.AddMonths(-1), dayStart.Offset);
            var to = new DateTimeOffset(monthAnchor.AddMonths(2), dayStart.Offset);

            var active = await _context.Api.GetMeetingsAsync(_context.Organization.Id);
            var calendar = await _context.Api.GetCalendarAsync(_context.Organization.Id, from, to);

            Meetings.Clear();
            foreach (var meeting in active) Meetings.Add(new MeetingViewModel(meeting, _context.People));

            Upcoming.Clear();
            foreach (var meeting in calendar.Where(m => m.StartsAt >= dayStart && m.StartsAt < dayEnd.AddDays(7)))
                Upcoming.Add(new MeetingViewModel(meeting, _context.People));

            _scheduledMeetings.Clear();
            foreach (var meeting in calendar.OrderBy(m => m.StartsAt))
                _scheduledMeetings.Add(new MeetingViewModel(meeting, _context.People));

            InviteCandidates.Clear();
            foreach (var person in _context.People.People.Where(p => !p.IsMe)) InviteCandidates.Add(person);

            RebuildMonthGrid();
            RefreshSelectedDayMeetings();

            if (Stage is { } stage)
            {
                var current = Meetings.Concat(Upcoming).Concat(_scheduledMeetings).FirstOrDefault(m => m.Id == stage.Id)
                              ?? (calendar.FirstOrDefault(m => m.Id == stage.Id) is { } dto
                                  ? new MeetingViewModel(dto, _context.People)
                                  : null);
                if (current is null)
                {
                    stage.Detach();
                    Stage = null;
                }
                else stage.Meeting = current;
            }

            ErrorMessage = null;
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

    [RelayCommand]
    private void PreviousDay() =>
        ScheduleSelectedDate = SelectedDay.AddDays(-1).LocalDateTime.Date;

    [RelayCommand]
    private void NextDay() =>
        ScheduleSelectedDate = SelectedDay.AddDays(1).LocalDateTime.Date;

    [RelayCommand]
    private void PreviousMonth() => DisplayMonth = DisplayMonth.AddMonths(-1);

    [RelayCommand]
    private void NextMonth() => DisplayMonth = DisplayMonth.AddMonths(1);

    [RelayCommand]
    private void SelectDay(CalendarDayCellViewModel day)
    {
        if (day.Date.Year != DisplayMonth.Year || day.Date.Month != DisplayMonth.Month)
            DisplayMonth = new DateTime(day.Date.Year, day.Date.Month, 1);
        ScheduleSelectedDate = day.Date;
    }

    partial void OnDisplayMonthChanged(DateTime value)
    {
        RebuildMonthGrid();
        _ = LoadAsync();
    }

    partial void OnScheduleSelectedDateChanged(DateTime? value)
    {
        if (value is null) return;

        var day = DateOnly.FromDateTime(value.Value);
        var time = TimeOnly.FromDateTime(DateTimeOffset.Now.AddHours(1).LocalDateTime);
        if (DateTimeOffset.TryParse(ScheduleStartsAtText, out var current))
            time = TimeOnly.FromDateTime(current.LocalDateTime);

        var local = day.ToDateTime(time);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        ScheduleStartsAtText = local.ToString("yyyy-MM-dd HH:mm");
        SelectedDay = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), offset);
        SelectedDayLabel = SelectedDay.ToLocalTime().ToString("dddd, d MMMM");
        OnPropertyChanged(nameof(SelectedDayMeetingsHeading));
        RebuildMonthGrid();
        RefreshSelectedDayMeetings();
        _ = LoadAsync();
    }

    private void RebuildMonthGrid()
    {
        var month = DisplayMonth == default
            ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
            : new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1);
        var first = month;
        var start = first.AddDays(-(int)first.DayOfWeek); // Sunday-first grid
        var selected = ScheduleSelectedDate?.Date;
        var meetingDays = _scheduledMeetings
            .Select(m => m.StartsAt.ToLocalTime().Date)
            .ToHashSet();

        MonthDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            MonthDays.Add(new CalendarDayCellViewModel(
                date,
                isCurrentMonth: date.Month == month.Month,
                hasMeetings: meetingDays.Contains(date),
                isSelected: selected == date));
        }
    }

    private void RefreshSelectedDayMeetings()
    {
        SelectedDayMeetings.Clear();
        if (ScheduleSelectedDate is not { } selected) return;

        var day = selected.Date;
        foreach (var meeting in _scheduledMeetings
                     .Where(m => m.StartsAt.ToLocalTime().Date == day)
                     .OrderBy(m => m.StartsAt))
            SelectedDayMeetings.Add(meeting);

        OnPropertyChanged(nameof(HasSelectedDayMeetings));
        OnPropertyChanged(nameof(SelectedDayMeetingsHeading));
    }

    [RelayCommand]
    private async Task MeetNowAsync()
    {
        ErrorMessage = null;
        try
        {
            var title = string.IsNullOrWhiteSpace(NewMeetingTitle)
                ? $"{_context.People.Me.DisplayName}'s meeting"
                : NewMeetingTitle;
            var created = await _context.Api.CreateMeetingAsync(_context.Organization.Id, title);
            NewMeetingTitle = string.Empty;
            await JoinAsync(new MeetingViewModel(created, _context.People));
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task ScheduleAsync()
    {
        ErrorMessage = null;
        try
        {
            if (!DateTimeOffset.TryParse(ScheduleStartsAtText, out var startsAt))
                throw new ApiException("Enter a valid start time, e.g. 2026-11-24 15:00.");
            var invitees = InviteCandidates.Where(p => p.IsSelected).Select(p => p.Id).ToList();
            await _context.Api.ScheduleMeetingAsync(
                _context.Organization.Id,
                new ScheduleMeetingRequest(
                    ScheduleTitle, ScheduleDescription, startsAt, startsAt.AddHours(1),
                    ScheduleLobbyEnabled, invitees));
            ScheduleTitle = string.Empty;
            ScheduleDescription = string.Empty;
            foreach (var person in InviteCandidates) person.IsSelected = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task RespondAcceptAsync(MeetingViewModel meeting) =>
        await RespondAsync(meeting, InviteStatus.Accepted);

    [RelayCommand]
    private async Task RespondDeclineAsync(MeetingViewModel meeting) =>
        await RespondAsync(meeting, InviteStatus.Declined);

    private async Task RespondAsync(MeetingViewModel meeting, InviteStatus status)
    {
        ErrorMessage = null;
        try
        {
            await _context.Api.RespondToInviteAsync(meeting.Id, status);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task JoinAsync(MeetingViewModel meeting)
    {
        ErrorMessage = null;
        try
        {
            var join = await _context.Api.JoinMeetingAsync(meeting.Id);
            Stage?.Detach();
            Stage = new MeetingStageViewModel(meeting, join, _context);
            if (!join.InLobby) Stage.OpenMediaCommand.Execute(null);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task LeaveAsync()
    {
        if (Stage is not { } stage) return;
        stage.Detach();
        Stage = null;
        try
        {
            await _context.Api.LeaveMeetingAsync(stage.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task EndAsync()
    {
        if (Stage is not { } stage) return;
        try
        {
            await _context.Api.EndMeetingAsync(stage.Id);
            stage.Detach();
            Stage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }

        await LoadAsync();
    }

    private void OnMeetingsChanged() => _ = LoadAsync();

    private void OnMeetingUpdated(MeetingDto meeting)
    {
        if (Stage?.Id == meeting.Id) Stage.Meeting.Apply(meeting, _context.People);
    }
}

// ---- Settings ---------------------------------------------------------------

public sealed record AvailabilityOption(PresenceStatus Status, string Label);

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private readonly Func<Task> _signOut;
    private readonly Func<Task> _onLeftOrganization;
    private bool _applyingAvailability;

    public SettingsViewModel(
        SessionContext context, MeDto me, AppConfig config, Func<Task> signOut, Func<Task> onLeftOrganization)
    {
        _context = context;
        _signOut = signOut;
        _onLeftOrganization = onLeftOrganization;
        Email = me.Email;
        _displayName = me.DisplayName;
        _statusMessage = me.StatusMessage ?? string.Empty;
        _availability = AvailabilityOptions.First(o => o.Status == me.Availability);
        ServerUrl = config.ApiBaseUri.ToString();
    }

    public IReadOnlyList<AvailabilityOption> AvailabilityOptions { get; } =
    [
        new(PresenceStatus.Available, "Available"),
        new(PresenceStatus.Away, "Away"),
        new(PresenceStatus.Busy, "Busy"),
        new(PresenceStatus.DoNotDisturb, "Do not disturb"),
    ];

    public string Email { get; }
    public string ServerUrl { get; }
    public string OrganizationName => _context.Organization.Name;
    public string RoleText => _context.Organization.Role.ToString();
    public string? JoinCode => _context.Organization.JoinCode;
    public bool CanInvite => JoinCode is not null;

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _statusMessage;
    [ObservableProperty] private string? _profileStatus;
    [ObservableProperty] private string? _inviteStatus;
    [ObservableProperty] private string? _leaveStatus;
    [ObservableProperty] private AvailabilityOption _availability;

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        ProfileStatus = null;
        try
        {
            var me = await _context.Api.UpdateProfileAsync(new UpdateProfileRequest(DisplayName, StatusMessage));
            DisplayName = me.DisplayName;
            StatusMessage = me.StatusMessage ?? string.Empty;
            _context.People.Me.DisplayName = me.DisplayName;
            _context.People.Me.StatusMessage = me.StatusMessage;
            ProfileStatus = "Saved";
        }
        catch (Exception ex)
        {
            ProfileStatus = Describe.Error(ex);
        }
    }

    partial void OnAvailabilityChanged(AvailabilityOption? oldValue, AvailabilityOption newValue)
    {
        if (_applyingAvailability || oldValue is null || newValue is null) return;
        _ = ApplyAvailabilityAsync(oldValue, newValue);
    }

    private async Task ApplyAvailabilityAsync(AvailabilityOption previous, AvailabilityOption chosen)
    {
        try
        {
            await _context.Realtime.SetAvailabilityAsync(chosen.Status);
            ProfileStatus = null;
        }
        catch (Exception ex)
        {
            // Show what the server still has, not what the user asked for.
            _applyingAvailability = true;
            Availability = previous;
            _applyingAvailability = false;
            ProfileStatus = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task CopyJoinCodeAsync()
    {
        if (JoinCode is null) return;
        await _context.Platform.CopyTextAsync(JoinCode);
        InviteStatus = "Copied";
    }

    [RelayCommand]
    private async Task LeaveOrganizationAsync()
    {
        LeaveStatus = null;
        var confirmed = await _context.Platform.ConfirmAsync(
            "Leave organization?",
            $"Leave {_context.Organization.Name}? You will lose access to its chats, channels, and meetings until you join again.",
            "Leave",
            "Cancel");
        if (!confirmed) return;

        try
        {
            await _context.Api.LeaveOrganizationAsync(_context.Organization.Id);
            await _onLeftOrganization();
        }
        catch (Exception ex)
        {
            LeaveStatus = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private Task SignOutAsync() => _signOut();
}
