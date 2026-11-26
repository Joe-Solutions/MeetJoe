using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Controls;
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
    public RecurrenceKind Recurrence { get; private set; }
    public IReadOnlyList<PersonViewModel> Participants { get; private set; } = [];
    public IReadOnlyList<PersonViewModel> Lobby { get; private set; } = [];
    public IReadOnlyList<MeetingInviteDto> Invites { get; private set; } = [];
    public IReadOnlyList<Guid> RaisedHands { get; private set; } = [];
    public IReadOnlyList<ReadCursorDto> ChatReadCursors { get; private set; } = [];
    public string Summary { get; private set; } = string.Empty;
    public string WhenText { get; private set; } = string.Empty;
    public string RecurrenceText { get; private set; } = string.Empty;
    public bool HasRecurrence => RecurrenceText.Length > 0;
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
        Recurrence = meeting.Recurrence;
        Participants = meeting.Participants.Select(people.Get).ToList();
        Lobby = meeting.Lobby.Select(people.Get).ToList();
        Invites = meeting.Invites;
        RaisedHands = meeting.RaisedHands;
        ChatReadCursors = meeting.ChatReadCursors ?? [];
        MyInviteStatus = HasEnded
            ? null
            : meeting.Invites.FirstOrDefault(i => i.UserId == people.MyId)?.Status;
        WhenText = meeting.StartsAt.ToLocalTime().ToString("ddd d MMM · h:mm tt");
        RecurrenceText = meeting.Recurrence switch
        {
            RecurrenceKind.Daily => meeting.RecurrenceUntil is { } until
                ? $"Repeats daily until {until.ToLocalTime():d MMM yyyy}"
                : "Repeats daily",
            RecurrenceKind.Weekly => meeting.RecurrenceUntil is { } until
                ? $"Repeats weekly until {until.ToLocalTime():d MMM yyyy}"
                : "Repeats weekly",
            _ => string.Empty,
        };
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
    private readonly Dictionary<Guid, DateTimeOffset> _chatReadAt = [];

    public MeetingStageViewModel(
        MeetingViewModel meeting, MeetingJoinDto join, SessionContext context,
        string? guestInviteToken = null)
    {
        _meeting = meeting;
        _context = context;
        GuestInviteToken = guestInviteToken;
        InLobby = join.InLobby;
        MediaServer = join.ServerUrl;
        RoomName = join.RoomName;
        MediaPageUri = BuildMediaPageUri(context.Config.ApiBaseUri, join);
        context.Realtime.MeetingUpdated += OnMeetingUpdated;
        context.Realtime.MeetingChat += OnMeetingChat;
        context.Realtime.ParticipantRemoved += OnRemoved;
        context.Realtime.ReadReceipt += OnReadReceipt;
        _ = LoadChatAsync();
        if (InLobby) _ = WatchLobbyAdmitAsync();
    }

    /// <summary>Email/deep-link invite token used to refresh LiveKit creds after lobby admit.</summary>
    public string? GuestInviteToken { get; }

    public Guid Id => Meeting.Id;

    [ObservableProperty] private MeetingViewModel _meeting;
    [ObservableProperty] private bool _inLobby;
    [ObservableProperty] private Uri _mediaPageUri = null!;
    [ObservableProperty] private string _chatDraft = string.Empty;
    [ObservableProperty] private bool _handRaised;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectingTiles))]
    private bool _showAddPeople;
    [ObservableProperty] private string? _addPeopleStatus;
    [ObservableProperty] private bool _isMediaOpen;

    [ObservableProperty] private bool _isCameraOn = true;
    [ObservableProperty] private bool _isMicOn = true;
    [ObservableProperty] private bool _isScreenShareOn;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecordingIndicator))]
    private bool _isRecording;

    /// <summary>True when another participant’s host is recording this room (we are not the recorder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecordingIndicator))]
    private bool _isRoomBeingRecorded;

    [ObservableProperty] private string _recordingElapsed = "00:00";
    [ObservableProperty] private string? _recordingNotice;

    public bool ShowRecordingIndicator => IsRecording || IsRoomBeingRecorded;

    private DispatcherTimer? _recordingUiTimer;
    private DateTimeOffset _recordingStartedAt;

    /// <summary>Elapsed since LiveKit session start (shared 1 hour limit).</summary>
    [ObservableProperty] private string _sessionElapsed = "00:00";
    [ObservableProperty] private string? _sessionRemaining;
    [ObservableProperty] private bool _sessionWarningActive;
    [ObservableProperty] private string? _sessionNotice;
    [ObservableProperty] private bool _showSessionTimer;

    private DispatcherTimer? _sessionUiTimer;
    private DateTimeOffset _sessionStartedAt;
    private bool _sessionExpiryHandled;
    public const int SessionLimitMinutes = 60;
    public const int SessionWarnMinutes = 5;

    /// <summary>True once LiveKit reports media-ready; Avalonia tiles hide so the WebView can paint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectingTiles))]
    private bool _isLiveKitSurfaceReady;

    /// <summary>
    /// Avalonia participant tiles while LiveKit is connecting, or while the native WebView is
    /// hidden so the Add people dialog can paint above it on macOS.
    /// </summary>
    public bool ShowConnectingTiles => ShowAddPeople || !IsLiveKitSurfaceReady;

    /// <summary>Wired by the meeting stage view to the LiveKit WebView host.</summary>
    public Func<Task>? ToggleCameraHandler { get; set; }
    public Func<Task>? ToggleMicHandler { get; set; }
    public Func<Task>? ToggleScreenShareHandler { get; set; }
    public Func<string, string, string, Task<bool>>? StartRecordingHandler { get; set; }
    public Func<Task<Guid?>>? StopRecordingHandler { get; set; }
    /// <summary>Fan meeting chat out to web guests in the LiveKit room.</summary>
    public Func<string, string, string?, long, Task>? PublishChatHandler { get; set; }

    /// <summary>Host-only recording control.</summary>
    public bool CanRecord => Meeting.IsHostedByMe && IsMediaOpen && !InLobby;

    /// <summary>Raised so the stage can scroll chat to the latest bubble (own send / open chat).</summary>
    public event Action? ChatFollowRequested;

    public void ApplyActiveSpeakers(IReadOnlyCollection<Guid> speakerIds)
    {
        var set = speakerIds as HashSet<Guid> ?? speakerIds.ToHashSet();
        foreach (var person in Meeting.Participants)
            person.IsSpeaking = set.Contains(person.Id);
    }

    public void ApplyMutedParticipants(IReadOnlyCollection<Guid> mutedIds)
    {
        var set = mutedIds as HashSet<Guid> ?? mutedIds.ToHashSet();
        foreach (var person in Meeting.Participants)
        {
            if (person.IsMe)
                person.IsMicMuted = !IsMicOn || set.Contains(person.Id);
            else
                person.IsMicMuted = set.Contains(person.Id);
        }
    }

    public void MarkLiveKitSurfaceReady() => IsLiveKitSurfaceReady = true;

    partial void OnIsMicOnChanged(bool value)
    {
        foreach (var person in Meeting.Participants.Where(p => p.IsMe))
            person.IsMicMuted = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInviteSuggestions))]
    private string _inviteQuery = string.Empty;

    /// <summary>Right rail: people roster (Teams/Zoom-style) vs meeting chat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatPanel))]
    private bool _showPeoplePanel = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnreadChat))]
    [NotifyPropertyChangedFor(nameof(UnreadChatBadge))]
    private int _unreadChatCount;

    public bool ShowChatPanel => !ShowPeoplePanel;
    public bool HasUnreadChat => UnreadChatCount > 0;
    public string UnreadChatBadge => UnreadChatCount > 99 ? "99+" : UnreadChatCount.ToString();
    public bool HasInviteSuggestions => InviteSuggestions.Count > 0;

    /// <summary>Meet-style tile columns for the center participant grid.</summary>
    public int ParticipantGridColumns =>
        Meeting.Participants.Count switch
        {
            <= 1 => 1,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4,
        };

    public ObservableCollection<RoomChatMessageViewModel> ChatMessages { get; } = [];
    public ObservableCollection<PendingAttachmentViewModel> PendingAttachments { get; } = [];
    public ObservableCollection<PersonViewModel> InviteSuggestions { get; } = [];
    public IReadOnlyList<string> EmojiPalette => RoomChatMessageViewModel.EmojiPalette;

    [ObservableProperty] private bool _showEmojiPicker;
    [ObservableProperty] private bool _hasPendingAttachments;

    partial void OnInviteQueryChanged(string value) => RefreshInviteSuggestions();
    partial void OnMeetingChanged(MeetingViewModel value)
    {
        OnPropertyChanged(nameof(ParticipantGridColumns));
        OnPropertyChanged(nameof(CanRecord));
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Where the media room lives, shown so a connection can be diagnosed.</summary>
    public string MediaServer { get; private set; }

    public string RoomName { get; private set; }

    public void Detach()
    {
        IsMediaOpen = false;
        IsLiveKitSurfaceReady = false;
        UnreadChatCount = 0;
        ShowEmojiPicker = false;
        PendingAttachments.Clear();
        HasPendingAttachments = false;
        IsScreenShareOn = false;
        IsRecording = false;
        StopRecordingUiTimer();
        StopSessionUiTimer();
        ToggleCameraHandler = null;
        ToggleMicHandler = null;
        ToggleScreenShareHandler = null;
        StartRecordingHandler = null;
        StopRecordingHandler = null;
        PublishChatHandler = null;
        _context.Realtime.MeetingUpdated -= OnMeetingUpdated;
        _context.Realtime.MeetingChat -= OnMeetingChat;
        _context.Realtime.ParticipantRemoved -= OnRemoved;
        _context.Realtime.ReadReceipt -= OnReadReceipt;
        _chatReadAt.Clear();
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
    private void ShowChat()
    {
        ShowPeoplePanel = false;
        UnreadChatCount = 0;
        RequestChatFollow();
        _ = MarkMeetingChatReadAsync();
    }

    partial void OnShowPeoplePanelChanged(bool value)
    {
        if (!value)
        {
            UnreadChatCount = 0;
            RequestChatFollow();
            _ = MarkMeetingChatReadAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleCameraAsync()
    {
        // Optimistic chrome update; MediaStateChanged corrects from the WebView.
        IsCameraOn = !IsCameraOn;
        if (ToggleCameraHandler is not null) await ToggleCameraHandler.Invoke();
    }

    [RelayCommand]
    private async Task ToggleMicAsync()
    {
        // Optimistic chrome update; MediaStateChanged corrects from the WebView.
        IsMicOn = !IsMicOn;
        if (ToggleMicHandler is not null) await ToggleMicHandler.Invoke();
    }

    [RelayCommand]
    private async Task ToggleScreenShareAsync()
    {
        // Optimistic chrome update; MediaStateChanged corrects from the WebView.
        IsScreenShareOn = !IsScreenShareOn;
        if (ToggleScreenShareHandler is not null) await ToggleScreenShareHandler.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private async Task ToggleRecordingAsync()
    {
        if (!CanRecord) return;
        if (IsRecording)
            await FinalizeRecordingAsync();
        else
            await BeginRecordingAsync();
    }

    /// <summary>Stop capture, upload, announce in chat, and mark recording ready. Safe on leave/end.</summary>
    public async Task FinalizeRecordingAsync()
    {
        // Leave must stay snappy when the host never started a recording.
        if (!IsRecording) return;
        Guid? fileId = null;
        try
        {
            if (StopRecordingHandler is not null)
                fileId = await StopRecordingHandler.Invoke();
        }
        catch
        {
            fileId = null;
        }

        IsRecording = false;
        StopRecordingUiTimer();
        ToggleRecordingCommand.NotifyCanExecuteChanged();

        if (fileId is { } id)
        {
            try
            {
                var title = string.IsNullOrWhiteSpace(Meeting.Title) ? "Meeting" : Meeting.Title;
                await _context.Api.SendMeetingChatAsync(
                    Id, $"Recording saved: {title}", attachmentIds: [id]);
                await _context.Api.CompleteRecordingAsync(Id, id);
                RecordingNotice = "Recording saved to meeting chat and Files.";
                RequestChatFollow();
            }
            catch
            {
                try { await _context.Api.CancelRecordingAsync(Id); }
                catch { /* best-effort */ }
                RecordingNotice = "Recording uploaded but could not post to chat.";
            }
        }
        else
        {
            try { await _context.Api.CancelRecordingAsync(Id); }
            catch { /* no in-progress recording */ }
            RecordingNotice = "Recording stopped — file was not saved.";
        }
    }

    private async Task BeginRecordingAsync()
    {
        if (StartRecordingHandler is null) return;
        try
        {
            await _context.Api.StartRecordingAsync(Id);
            var token = await _context.Api.GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new ApiException("Sign in again to record.");

            var uploadUrl = new Uri(
                _context.Config.ApiBaseUri, $"api/meetings/{Id:D}/recording/upload").ToString();
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var safeTitle = string.Join("_", (Meeting.Title ?? "meeting").Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{safeTitle}-{stamp}.webm";
            var started = await StartRecordingHandler.Invoke(uploadUrl, token, fileName);
            IsRecording = started;
            if (started)
            {
                RecordingNotice = "Recording started — participants notified";
                StartRecordingUiTimer(DateTimeOffset.Now);
            }
            else
            {
                try { await _context.Api.CancelRecordingAsync(Id); }
                catch { /* ignore */ }
            }
        }
        catch (Exception)
        {
            IsRecording = false;
            StopRecordingUiTimer();
            try { await _context.Api.CancelRecordingAsync(Id); }
            catch { /* ignore */ }
        }

        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    public void ApplyLocalRecordingTimer(long? startedAtUnixMs)
    {
        if (startedAtUnixMs is { } ms && ms > 0)
        {
            IsRecording = true;
            StartRecordingUiTimer(DateTimeOffset.FromUnixTimeMilliseconds(ms));
        }
        else if (!IsRoomBeingRecorded)
        {
            StopRecordingUiTimer();
        }
    }

    public void ApplyRoomRecording(bool active, string? byName, long? startedAtUnixMs)
    {
        if (IsRecording)
        {
            // Local host already owns the indicator.
            if (!active) return;
            return;
        }

        IsRoomBeingRecorded = active;
        if (active)
        {
            var who = string.IsNullOrWhiteSpace(byName) ? "The host" : byName.Trim();
            RecordingNotice = $"{who} started recording this meeting";
            var started = startedAtUnixMs is { } ms && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : DateTimeOffset.Now;
            StartRecordingUiTimer(started);
        }
        else
        {
            RecordingNotice = "Recording stopped";
            StopRecordingUiTimer();
        }
    }

    private void StartRecordingUiTimer(DateTimeOffset startedAt)
    {
        _recordingStartedAt = startedAt;
        TickRecordingElapsed();
        if (_recordingUiTimer is not null) return;
        _recordingUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recordingUiTimer.Tick += (_, _) => TickRecordingElapsed();
        _recordingUiTimer.Start();
    }

    private void StopRecordingUiTimer()
    {
        _recordingUiTimer?.Stop();
        _recordingUiTimer = null;
        RecordingElapsed = "00:00";
        IsRoomBeingRecorded = false;
        if (!IsRecording)
            RecordingNotice = null;
    }

    private void TickRecordingElapsed()
    {
        var elapsed = DateTimeOffset.Now - _recordingStartedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        RecordingElapsed = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    partial void OnIsRecordingChanged(bool value)
    {
        if (!value && !IsRoomBeingRecorded)
            StopRecordingUiTimer();
        OnPropertyChanged(nameof(ShowRecordingIndicator));
    }

    public void ApplySessionStart(long startedAtUnixMs)
    {
        if (startedAtUnixMs <= 0) return;
        _sessionStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(startedAtUnixMs);
        _sessionExpiryHandled = false;
        ShowSessionTimer = true;
        SessionWarningActive = false;
        TickSessionUi();
        if (_sessionUiTimer is not null) return;
        _sessionUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _sessionUiTimer.Tick += (_, _) => TickSessionUi();
        _sessionUiTimer.Start();
    }

    public void ApplySessionWarning()
    {
        SessionWarningActive = true;
        SessionNotice = "5 minutes remaining — session ends at 1 hour.";
    }

    /// <summary>Returns true once when the page reports the hard limit (caller should leave).</summary>
    public bool ConsumeSessionExpired()
    {
        if (_sessionExpiryHandled) return false;
        _sessionExpiryHandled = true;
        SessionWarningActive = true;
        SessionNotice = "Time’s up — this meeting reached the 1 hour limit.";
        SessionRemaining = "00:00 left";
        return true;
    }

    public void StopSessionUiTimer()
    {
        _sessionUiTimer?.Stop();
        _sessionUiTimer = null;
        ShowSessionTimer = false;
        SessionElapsed = "00:00";
        SessionRemaining = null;
        SessionWarningActive = false;
        SessionNotice = null;
        _sessionExpiryHandled = false;
    }

    private void TickSessionUi()
    {
        var elapsed = DateTimeOffset.Now - _sessionStartedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var limit = TimeSpan.FromMinutes(SessionLimitMinutes);
        var remaining = limit - elapsed;
        SessionElapsed = FormatClock(elapsed);
        if (remaining <= TimeSpan.FromMinutes(SessionWarnMinutes))
        {
            SessionWarningActive = true;
            SessionRemaining = FormatClock(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) + " left";
            if (remaining <= TimeSpan.Zero && string.IsNullOrEmpty(SessionNotice))
                SessionNotice = "Time’s up — this meeting reached the 1 hour limit.";
        }
        else
        {
            SessionRemaining = null;
        }
    }

    private static string FormatClock(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");

    partial void OnIsMediaOpenChanged(bool value) => ToggleRecordingCommand.NotifyCanExecuteChanged();
    partial void OnInLobbyChanged(bool value) => ToggleRecordingCommand.NotifyCanExecuteChanged();

    private void RequestChatFollow() => ChatFollowRequested?.Invoke();

    private bool _permissionDialogOpen;

    public async Task NotifyMediaPermissionNeededAsync(string kind)
    {
        if (_permissionDialogOpen) return;
        _permissionDialogOpen = true;
        try
        {
            switch (kind)
            {
                case "mic":
                    await _context.Platform.AlertAsync(
                        "Microphone access needed",
                        "MeetJoe is not allowed to use your microphone.\n\n1. Open System Settings → Privacy & Security → Microphone\n2. Turn on MeetJoe (or “dotnet” if listed while debugging)\n3. Leave this meeting and join again.");
                    break;
                case "camera":
                    await _context.Platform.AlertAsync(
                        "Camera access needed",
                        "MeetJoe is not allowed to use your camera, so video cannot turn on.\n\n1. Open System Settings → Privacy & Security → Camera\n2. Turn on MeetJoe\n3. Leave this meeting and join again.\n\nTip: launch MeetJoe.app (not a raw binary) so macOS can show the camera permission dialog.");
                    try { await _context.Platform.OpenUriAsync(new Uri("x-apple.systempreferences:com.apple.preference.security?Privacy_Camera")); }
                    catch { /* best-effort */ }
                    break;
                case "share":
                    await _context.Platform.AlertAsync(
                        "Screen sharing needed",
                        "MeetJoe could not share your screen.\n\n1. Open System Settings → Privacy & Security → Screen Recording\n2. Turn on MeetJoe\n3. Try Share screen again.\n\nIf you cancelled the system picker, just tap Share screen again.");
                    try { await _context.Platform.OpenUriAsync(new Uri("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")); }
                    catch { /* best-effort */ }
                    break;
                default:
                    await _context.Platform.AlertAsync(
                        "Microphone & camera access needed",
                        "MeetJoe needs permission to use your microphone and camera.\n\n1. Open System Settings → Privacy & Security\n2. Enable MeetJoe under Microphone and Camera\n3. Leave this meeting and join again.");
                    break;
            }
        }
        finally
        {
            _permissionDialogOpen = false;
        }
    }

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
    private void ToggleEmojiPicker() => ShowEmojiPicker = !ShowEmojiPicker;

    [RelayCommand]
    private void InsertEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        ChatDraft += emoji;
        ShowEmojiPicker = false;
    }

    [RelayCommand]
    private async Task AttachChatFileAsync()
    {
        if (InLobby) return;
        try
        {
            var picked = await _context.Platform.PickFileAsync();
            if (picked is null) return;
            await AttachFilesAsync([picked]);
        }
        catch
        {
            // Keep composing; attach failures are transient.
        }
    }

    /// <summary>Upload files from Attach or drag-drop onto the meeting chat composer.</summary>
    public async Task AttachFilesAsync(IReadOnlyList<PickedFile> files)
    {
        if (InLobby || files.Count == 0) return;
        try
        {
            foreach (var picked in files)
            {
                await using var content = await picked.OpenReadAsync();
                var file = await _context.Api.UploadFileAsync(null, null, picked.Name, content, meetingId: Id);
                PendingAttachments.Add(new PendingAttachmentViewModel(file));
            }

            HasPendingAttachments = PendingAttachments.Count > 0;
        }
        catch
        {
            // Keep composing; attach failures are transient.
        }
    }

    [RelayCommand]
    private void RemoveChatAttachment(PendingAttachmentViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        HasPendingAttachments = PendingAttachments.Count > 0;
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        if (InLobby) return;
        if (string.IsNullOrWhiteSpace(ChatDraft) && PendingAttachments.Count == 0) return;

        var body = ChatDraft;
        var attachments = PendingAttachments.ToList();
        ChatDraft = string.Empty;
        PendingAttachments.Clear();
        HasPendingAttachments = false;
        ShowEmojiPicker = false;
        try
        {
            var ids = attachments.Select(a => a.FileId).ToList();
            var sent = _context.Realtime.IsConnected
                ? await _context.Realtime.SendMeetingChatAsync(Id, body, ids)
                : await _context.Api.SendMeetingChatAsync(Id, body, attachmentIds: ids);
            RoomChatMessageViewModel.TryAdd(ChatMessages, sent, _context);
            RefreshChatSeenBy();
            RequestChatFollow();
            if (PublishChatHandler is not null && !string.IsNullOrWhiteSpace(sent.Body))
            {
                try
                {
                    await PublishChatHandler.Invoke(
                        sent.Id.ToString("D"),
                        sent.Body,
                        sent.Sender.DisplayName,
                        sent.CreatedAt.ToUnixTimeMilliseconds());
                }
                catch
                {
                    // LiveKit fan-out is best-effort for web guests.
                }
            }
        }
        catch
        {
            ChatDraft = body;
            foreach (var attachment in attachments) PendingAttachments.Add(attachment);
            HasPendingAttachments = PendingAttachments.Count > 0;
        }
    }

    private async Task LoadChatAsync()
    {
        try
        {
            var messages = await _context.Api.GetMeetingMessagesAsync(Id);
            ChatMessages.Clear();
            foreach (var message in messages)
                ChatMessages.Add(new RoomChatMessageViewModel(message, _context));
            RoomChatMessageViewModel.ApplyGrouping(ChatMessages);
            ApplyMeetingReadCursors(Meeting);
            RefreshChatSeenBy();
            RequestChatFollow();
            if (ShowChatPanel && !InLobby) await MarkMeetingChatReadAsync();
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
        OnPropertyChanged(nameof(ParticipantGridColumns));
        HandRaised = meeting.RaisedHands.Contains(_context.People.MyId);
        var wasInLobby = InLobby;
        InLobby = meeting.Lobby.Any(p => p.Id == _context.People.MyId);
        if (wasInLobby && !InLobby)
            _ = EnterMeetingAfterAdmitAsync();
        if (InLobby) IsMediaOpen = false;
        if (ShowAddPeople) RefreshInviteSuggestions();
        if (meeting.ChatReadCursors is { } cursors)
        {
            foreach (var cursor in cursors)
                _chatReadAt[cursor.UserId] = cursor.ReadAt;
            RefreshChatSeenBy();
        }
    }

    private async Task WatchLobbyAdmitAsync()
    {
        // Guests (and members without MeetingUpdated) poll until the host admits them.
        while (InLobby)
        {
            try { await Task.Delay(2500); }
            catch { return; }
            if (!InLobby) return;
            try
            {
                MeetingJoinDto join;
                if (!string.IsNullOrWhiteSpace(GuestInviteToken))
                {
                    var session = await _context.Api.GetGuestMeetingSessionAsync(GuestInviteToken);
                    join = new MeetingJoinDto(
                        session.MeetingId, session.ServerUrl, session.RoomName, session.Token, session.InLobby);
                }
                else
                {
                    join = await _context.Api.JoinMeetingAsync(Id);
                }

                if (join.InLobby) continue;
                ApplyAdmittedJoin(join);
                return;
            }
            catch
            {
                // Keep waiting while the lobby is still open.
            }
        }
    }

    private async Task EnterMeetingAfterAdmitAsync()
    {
        try
        {
            MeetingJoinDto join;
            if (!string.IsNullOrWhiteSpace(GuestInviteToken))
            {
                var session = await _context.Api.GetGuestMeetingSessionAsync(GuestInviteToken);
                join = new MeetingJoinDto(
                    session.MeetingId, session.ServerUrl, session.RoomName, session.Token, session.InLobby);
            }
            else
            {
                join = await _context.Api.JoinMeetingAsync(Id);
            }

            if (join.InLobby)
            {
                InLobby = true;
                IsMediaOpen = false;
                return;
            }

            ApplyAdmittedJoin(join);
        }
        catch
        {
            // Fall back to opening media with the prior token if refresh fails.
            InLobby = false;
            IsMediaOpen = true;
        }
    }

    private void ApplyAdmittedJoin(MeetingJoinDto join)
    {
        InLobby = false;
        MediaServer = join.ServerUrl;
        RoomName = join.RoomName;
        MediaPageUri = BuildMediaPageUri(_context.Config.ApiBaseUri, join);
        IsMediaOpen = true;
    }

    private void OnMeetingChat(MessageDto message)
    {
        if (message.MeetingId != Id) return;
        if (!RoomChatMessageViewModel.TryAdd(ChatMessages, message, _context)) return;
        RefreshChatSeenBy();
        if (message.Sender.Id == _context.People.MyId)
            RequestChatFollow();
        else if (!ShowChatPanel)
            UnreadChatCount++;
        else
            _ = MarkMeetingChatReadAsync();

        // Fan guest/API chat into LiveKit so browser guests see desktop (and other) messages live.
        if (PublishChatHandler is not null
            && message.Sender.Id != _context.People.MyId
            && !string.IsNullOrWhiteSpace(message.Body))
        {
            _ = PublishChatHandler.Invoke(
                message.Id.ToString("D"),
                message.Body,
                message.Sender.DisplayName,
                message.CreatedAt.ToUnixTimeMilliseconds());
        }
    }

    private void OnReadReceipt(ReadReceiptDto receipt)
    {
        if (receipt.MeetingId != Id) return;
        ApplyChatReadReceipt(receipt.UserId, receipt.ReadAt);
    }

    private async Task MarkMeetingChatReadAsync()
    {
        if (InLobby) return;
        try
        {
            await _context.Api.MarkMeetingChatReadAsync(Id);
            ApplyChatReadReceipt(_context.People.MyId, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Best-effort.
        }
    }

    private void ApplyMeetingReadCursors(MeetingViewModel meeting)
    {
        foreach (var cursor in meeting.ChatReadCursors)
            _chatReadAt[cursor.UserId] = cursor.ReadAt;

        foreach (var person in meeting.Participants)
        {
            if (!_chatReadAt.ContainsKey(person.Id))
                _chatReadAt[person.Id] = DateTimeOffset.MinValue;
        }
    }

    private void ApplyChatReadReceipt(Guid userId, DateTimeOffset readAt)
    {
        if (_chatReadAt.TryGetValue(userId, out var existing) && existing >= readAt) return;
        _chatReadAt[userId] = readAt;
        RefreshChatSeenBy();
    }

    private void RefreshChatSeenBy()
    {
        var peers = Meeting.Participants.Select(p => (p.Id, p.DisplayName));
        RoomChatMessageViewModel.RefreshSeenBy(ChatMessages, _context.People.MyId, peers, _chatReadAt);
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
        var query = "u=" + Uri.EscapeDataString(join.ServerUrl)
            + "&t=" + Uri.EscapeDataString(join.Token)
            + "&embed=1";
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
        _scheduleRecurrence = RecurrenceOptions[0];
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
    public ObservableCollection<CalendarDayCellViewModel> MonthDays { get; } = [];
    public ObservableCollection<MeetingViewModel> SelectedDayMeetings { get; } = [];
    public ObservableCollection<PersonViewModel> ScheduleInvitees { get; } = [];
    public ObservableCollection<string> ScheduleGuestEmails { get; } = [];
    public ObservableCollection<PersonViewModel> ScheduleInviteSuggestions { get; } = [];

    public IReadOnlyList<RecurrenceOption> RecurrenceOptions { get; } =
    [
        new(RecurrenceKind.None, "Does not repeat"),
        new(RecurrenceKind.Daily, "Daily"),
        new(RecurrenceKind.Weekly, "Weekly"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _newMeetingTitle = string.Empty;
    [ObservableProperty] private string _scheduleTitle = string.Empty;
    [ObservableProperty] private string _scheduleDescription = string.Empty;
    [ObservableProperty] private string _scheduleStartsAtText = DateTimeOffset.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm");
    [ObservableProperty] private bool _scheduleLobbyEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecurrenceUntil))]
    private RecurrenceOption _scheduleRecurrence = null!;
    [ObservableProperty] private string _scheduleRecurrenceUntilText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduleInviteSuggestions))]
    private string _scheduleInviteEmail = string.Empty;
    [ObservableProperty] private string? _scheduleInviteStatus;
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
    public bool ShowRecurrenceUntil => ScheduleRecurrence.Kind != RecurrenceKind.None;
    public bool HasScheduleInvitees => ScheduleInvitees.Count > 0 || ScheduleGuestEmails.Count > 0;
    public bool HasScheduleInviteSuggestions => ScheduleInviteSuggestions.Count > 0;
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

            // Date grid + “On this day” are for scheduled items; Meet now rooms live under Happening now.
            _scheduledMeetings.Clear();
            foreach (var meeting in calendar.Where(m => m.IsScheduled).OrderBy(m => m.StartsAt))
                _scheduledMeetings.Add(new MeetingViewModel(meeting, _context.People));

            RebuildMonthGrid();
            RefreshSelectedDayMeetings();
            RefreshScheduleInviteSuggestions();

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
        var seen = new HashSet<Guid>();
        foreach (var meeting in _scheduledMeetings
                     .Where(m => m.StartsAt.ToLocalTime().Date == day)
                     .OrderBy(m => m.StartsAt))
        {
            SelectedDayMeetings.Add(meeting);
            seen.Add(meeting.Id);
        }

        // Same calendar day: also surface live Meet now rooms so the right rail matches Happening now.
        if (day == DateTime.Today)
        {
            foreach (var meeting in Meetings
                         .Where(m => m.IsActive && !seen.Contains(m.Id) && m.StartsAt.ToLocalTime().Date == day)
                         .OrderByDescending(m => m.StartsAt))
                SelectedDayMeetings.Add(meeting);
        }

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

    partial void OnScheduleRecurrenceChanged(RecurrenceOption value)
    {
        if (value.Kind == RecurrenceKind.None)
        {
            ScheduleRecurrenceUntilText = string.Empty;
            return;
        }

        if (!string.IsNullOrWhiteSpace(ScheduleRecurrenceUntilText)) return;
        if (!DateTimeOffset.TryParse(ScheduleStartsAtText, out var startsAt))
            startsAt = DateTimeOffset.Now.AddHours(1);
        var until = value.Kind == RecurrenceKind.Daily
            ? startsAt.AddDays(14)
            : startsAt.AddDays(56);
        ScheduleRecurrenceUntilText = until.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    partial void OnScheduleInviteEmailChanged(string value) => RefreshScheduleInviteSuggestions();

    [RelayCommand]
    private void PickScheduleInviteSuggestion(PersonViewModel person)
    {
        ScheduleInviteEmail = person.Email;
        AddScheduleInvite();
    }

    [RelayCommand]
    private void AddScheduleInvite() => TryAddScheduleInvite();

    private bool TryAddScheduleInvite()
    {
        ScheduleInviteStatus = null;
        ErrorMessage = null;
        var raw = ScheduleInviteEmail.Trim();
        if (raw.Length == 0)
        {
            ScheduleInviteStatus = "Enter an email address to invite someone.";
            ErrorMessage = ScheduleInviteStatus;
            return false;
        }

        var match = ResolveScheduleInvitee();
        if (match is not null)
        {
            if (ScheduleInvitees.All(p => p.Id != match.Id))
                ScheduleInvitees.Add(match);
            ScheduleInviteEmail = string.Empty;
            ScheduleInviteStatus = null;
            OnPropertyChanged(nameof(HasScheduleInvitees));
            RefreshScheduleInviteSuggestions();
            return true;
        }

        if (!LooksLikeEmail(raw))
        {
            ScheduleInviteStatus = "Enter a valid email address.";
            ErrorMessage = ScheduleInviteStatus;
            return false;
        }

        var email = raw.ToLowerInvariant();
        if (ScheduleGuestEmails.Any(e => e.Equals(email, StringComparison.OrdinalIgnoreCase))
            || ScheduleInvitees.Any(p => p.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            ScheduleInviteEmail = string.Empty;
            RefreshScheduleInviteSuggestions();
            return true;
        }

        ScheduleGuestEmails.Add(email);
        ScheduleInviteEmail = string.Empty;
        ScheduleInviteStatus = null;
        OnPropertyChanged(nameof(HasScheduleInvitees));
        RefreshScheduleInviteSuggestions();
        return true;
    }

    private static bool LooksLikeEmail(string value) =>
        value.Contains('@') && !value.Contains(' ') && value.IndexOf('@') is > 0 and var at
        && value.IndexOf('.', at) > at + 1;

    [RelayCommand]
    private void RemoveScheduleInvite(PersonViewModel person)
    {
        ScheduleInvitees.Remove(person);
        OnPropertyChanged(nameof(HasScheduleInvitees));
        RefreshScheduleInviteSuggestions();
    }

    [RelayCommand]
    private void RemoveScheduleGuestEmail(string email)
    {
        ScheduleGuestEmails.Remove(email);
        OnPropertyChanged(nameof(HasScheduleInvitees));
    }

    private PersonViewModel? ResolveScheduleInvitee()
    {
        var q = ScheduleInviteEmail.Trim();
        if (q.Length == 0) return null;

        var eligible = EligibleScheduleInvitees().ToList();
        return eligible.FirstOrDefault(p => p.Email.Equals(q, StringComparison.OrdinalIgnoreCase))
               ?? (ScheduleInviteSuggestions.Count == 1 ? ScheduleInviteSuggestions[0] : null);
    }

    private IEnumerable<PersonViewModel> EligibleScheduleInvitees()
    {
        var already = ScheduleInvitees.Select(p => p.Id).Append(_context.People.MyId).ToHashSet();
        return _context.People.People.Where(p =>
            !p.IsMe
            && !already.Contains(p.Id)
            && !string.IsNullOrWhiteSpace(p.Email));
    }

    private void RefreshScheduleInviteSuggestions()
    {
        ScheduleInviteSuggestions.Clear();
        var q = ScheduleInviteEmail.Trim();
        if (q.Length == 0)
        {
            OnPropertyChanged(nameof(HasScheduleInviteSuggestions));
            return;
        }

        foreach (var person in EligibleScheduleInvitees()
                     .Where(p => p.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(8))
            ScheduleInviteSuggestions.Add(person);

        OnPropertyChanged(nameof(HasScheduleInviteSuggestions));
    }

    [RelayCommand]
    private async Task ScheduleAsync()
    {
        ErrorMessage = null;
        ScheduleInviteStatus = null;
        try
        {
            if (string.IsNullOrWhiteSpace(ScheduleTitle))
                throw new ApiException("Enter a meeting title.");
            if (!DateTimeOffset.TryParse(ScheduleStartsAtText, out var startsAt))
                throw new ApiException("Enter a valid start time, e.g. 2026-11-24 15:00.");

            // If they typed an email but forgot Add, include it when it uniquely resolves.
            if (!string.IsNullOrWhiteSpace(ScheduleInviteEmail) && !TryAddScheduleInvite())
                return;

            DateTimeOffset? recurrenceUntil = null;
            var recurrence = ScheduleRecurrence.Kind;
            if (recurrence != RecurrenceKind.None)
            {
                if (!string.IsNullOrWhiteSpace(ScheduleRecurrenceUntilText))
                {
                    if (!DateTimeOffset.TryParse(ScheduleRecurrenceUntilText, out var until))
                        throw new ApiException("Enter a valid repeat-until time, e.g. 2026-12-31 17:00.");
                    if (until < startsAt)
                        throw new ApiException("Repeat until must be after the meeting start time.");
                    recurrenceUntil = until;
                }
            }

            var invitees = ScheduleInvitees.Select(p => p.Id).ToList();
            var inviteEmails = ScheduleInvitees.Select(p => p.Email)
                .Concat(ScheduleGuestEmails)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await _context.Api.ScheduleMeetingAsync(
                _context.Organization.Id,
                new ScheduleMeetingRequest(
                    ScheduleTitle.Trim(),
                    string.IsNullOrWhiteSpace(ScheduleDescription) ? null : ScheduleDescription.Trim(),
                    startsAt,
                    startsAt.AddHours(1),
                    ScheduleLobbyEnabled,
                    invitees,
                    recurrence,
                    recurrenceUntil,
                    inviteEmails));

            ScheduleTitle = string.Empty;
            ScheduleDescription = string.Empty;
            ScheduleLobbyEnabled = false;
            ScheduleRecurrence = RecurrenceOptions[0];
            ScheduleRecurrenceUntilText = string.Empty;
            ScheduleInviteEmail = string.Empty;
            ScheduleInvitees.Clear();
            ScheduleGuestEmails.Clear();
            OnPropertyChanged(nameof(HasScheduleInvitees));
            RefreshScheduleInviteSuggestions();
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

    /// <summary>Accept invite (when pending) and join — used by the shell meeting-invite toast.</summary>
    public async Task<bool> JoinFromInviteAsync(Guid meetingId)
    {
        ErrorMessage = null;
        try
        {
            await LoadAsync();
            var meeting = FindMeeting(meetingId);
            if (meeting is null)
            {
                ErrorMessage = "That meeting is no longer available.";
                return false;
            }

            if (meeting.MyInviteStatus == InviteStatus.Pending)
                await _context.Api.RespondToInviteAsync(meeting.Id, InviteStatus.Accepted);

            await JoinAsync(meeting);
            return Stage?.Id == meetingId;
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
            return false;
        }
    }

    /// <summary>Join from a meetjoe:// email deep-link token (guest or member).</summary>
    public async Task<bool> JoinFromInviteTokenAsync(string token)
    {
        ErrorMessage = null;
        try
        {
            var invite = await _context.Api.GetGuestMeetingInviteAsync(token);
            if (invite.HasEnded || !invite.IsActive)
            {
                ErrorMessage = "This meeting has ended.";
                return false;
            }

            var displayName = invite.SuggestedDisplayName
                              ?? _context.People.Me.DisplayName
                              ?? "Guest";
            var guest = await _context.Api.JoinGuestMeetingInviteAsync(token, displayName);
            var join = new MeetingJoinDto(
                guest.MeetingId, guest.ServerUrl, guest.RoomName, guest.Token, guest.InLobby);

            await LoadAsync();
            var meeting = FindMeeting(guest.MeetingId)
                          ?? new MeetingViewModel(
                              new MeetingDto(
                                  guest.MeetingId,
                                  invite.Title,
                                  invite.Description,
                                  new UserSummaryDto(Guid.Empty, invite.HostName),
                                  invite.StartsAt,
                                  invite.StartsAt,
                                  invite.EndsAt,
                                  invite.IsActive,
                                  invite.LobbyEnabled,
                                  true,
                                  RecurrenceKind.None,
                                  null,
                                  [],
                                  [],
                                  [],
                                  []),
                              _context.People);

            Stage?.Detach();
            Stage = new MeetingStageViewModel(meeting, join, _context, guestInviteToken: token);
            if (!join.InLobby) Stage.OpenMediaCommand.Execute(null);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
            return false;
        }
    }

    private MeetingViewModel? FindMeeting(Guid meetingId) =>
        Meetings.FirstOrDefault(m => m.Id == meetingId)
        ?? Upcoming.FirstOrDefault(m => m.Id == meetingId)
        ?? SelectedDayMeetings.FirstOrDefault(m => m.Id == meetingId)
        ?? _scheduledMeetings.FirstOrDefault(m => m.Id == meetingId);

    [RelayCommand]
    private async Task LeaveAsync()
    {
        if (Stage is not { } stage) return;
        try { await stage.FinalizeRecordingAsync(); }
        catch { /* best-effort before tear-down */ }
        stage.Detach();
        Stage = null;
        await MeetingMediaParking.ShutdownAsync();
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
            try { await stage.FinalizeRecordingAsync(); }
            catch { /* best-effort before tear-down */ }
            await _context.Api.EndMeetingAsync(stage.Id);
            stage.Detach();
            Stage = null;
            await MeetingMediaParking.ShutdownAsync();
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

public sealed record RecurrenceOption(RecurrenceKind Kind, string Label);

public sealed record AvailabilityOption(PresenceStatus Status, string Label);

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private readonly Func<Task> _signOut;
    private readonly Func<Task> _onLeftOrganization;
    private bool _applyingAvailability;

    private readonly IAppUpdateService _updates;

    public SettingsViewModel(
        SessionContext context,
        MeDto me,
        AppConfig config,
        Func<Task> signOut,
        Func<Task> onLeftOrganization,
        IAppUpdateService? updates = null)
    {
        _context = context;
        _signOut = signOut;
        _onLeftOrganization = onLeftOrganization;
        _updates = updates ?? new NullAppUpdateService();
        Email = me.Email;
        _displayName = me.DisplayName;
        _statusMessage = me.StatusMessage ?? string.Empty;
        _availability = AvailabilityOptions.First(o => o.Status == me.Availability);
        ServerUrl = config.ApiBaseUri.ToString();
        CurrentVersion = AppVersion.Current;
        VersionLabel = $"Version {CurrentVersion}";
        _context.People.Me.ApplyAvatar(me.AvatarUrl);
        Photo = _context.People.Me.Photo;
        _context.People.Me.PropertyChanged += OnMeChanged;
        RefreshUpdateStatus();
        _updates.StatusChanged += RefreshUpdateStatus;
    }

    private void OnMeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PersonViewModel.Photo) or nameof(PersonViewModel.DisplayName))
        {
            Photo = _context.People.Me.Photo;
            if (e.PropertyName == nameof(PersonViewModel.DisplayName))
                DisplayName = _context.People.Me.DisplayName;
        }
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
    public string CurrentVersion { get; }

    [ObservableProperty] private string _versionLabel = "Version";
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _statusMessage;
    [ObservableProperty] private string? _profileStatus;
    [ObservableProperty] private string? _inviteStatus;
    [ObservableProperty] private string? _leaveStatus;
    [ObservableProperty] private AvailabilityOption _availability;
    [ObservableProperty] private string _updateStatusText = "Checking for updates…";
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    private Avalonia.Media.Imaging.Bitmap? _photo;

    public bool HasPhoto => Photo is not null;

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
            _context.People.Me.ApplyAvatar(me.AvatarUrl);
            ProfileStatus = "Saved";
        }
        catch (Exception ex)
        {
            ProfileStatus = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task UploadAvatarAsync()
    {
        ProfileStatus = null;
        try
        {
            var picked = await _context.Platform.PickFileAsync();
            if (picked is null) return;
            await using var stream = await picked.OpenReadAsync();
            var me = await _context.Api.UploadAvatarAsync(picked.Name, stream);
            _context.People.Me.DisplayName = me.DisplayName;
            AvatarImages.Current?.Invalidate(me.Id);
            _context.People.Me.ApplyAvatar(me.AvatarUrl);
            if (_context.People.Me.Photo is null && me.AvatarUrl is not null)
            {
                // Force a fresh download into the shared cache.
                var bitmap = await AvatarImages.Current!.LoadAsync(me.Id, me.AvatarUrl);
                if (bitmap is not null)
                {
                    AvatarImages.Current.SetLocal(me.Id, me.AvatarUrl, bitmap);
                    _context.People.Me.Photo = bitmap;
                }
            }

            Photo = _context.People.Me.Photo;
            ProfileStatus = "Photo updated";
        }
        catch (Exception ex)
        {
            ProfileStatus = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task ClearAvatarAsync()
    {
        ProfileStatus = null;
        try
        {
            var me = await _context.Api.ClearAvatarAsync();
            AvatarImages.Current?.Invalidate(me.Id);
            _context.People.Me.ApplyAvatar(null);
            _context.People.Me.Photo = null;
            Photo = null;
            ProfileStatus = "Photo removed";
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
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdates = true;
        try
        {
            await _updates.CheckAsync(applyAutomatically: true);
        }
        finally
        {
            IsCheckingUpdates = false;
            RefreshUpdateStatus();
        }
    }

    private void RefreshUpdateStatus()
    {
        var status = _updates.Status;
        var installed = string.IsNullOrWhiteSpace(status.CurrentVersion) ? CurrentVersion : status.CurrentVersion;
        VersionLabel = $"Version {installed}";
        UpdateStatusText = status.State switch
        {
            AppUpdateState.Checking => status.Message ?? "Checking for updates…",
            AppUpdateState.Downloading => status.Message ?? $"Downloading… {status.DownloadPercent}%",
            AppUpdateState.Updating => status.Message ?? "Installing update…",
            AppUpdateState.Available => status.Message
                ?? $"Update {status.AvailableVersion} available — you are still on {installed}.",
            AppUpdateState.UpToDate => status.Message ?? $"You're on the latest release ({installed}).",
            AppUpdateState.Unsupported => status.Message
                ?? "Auto-update requires an installed MeetJoe build. Dev runs show this version only.",
            AppUpdateState.Failed => status.Message ?? "Update check failed.",
            _ => $"Installed build {installed}.",
        };
    }

    [RelayCommand]
    private Task SignOutAsync() => _signOut();
}
