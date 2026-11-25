using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Services;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public enum CallHistoryStatusKind
{
    Other,
    Missed,
    Rejected,
    Duration,
}

/// <summary>One row in the live call people roster (Teams/Zoom-style).</summary>
public partial class CallRosterItemViewModel : ObservableObject
{
    public CallRosterItemViewModel(CallParticipantDto participant, PeopleStore people)
    {
        UserId = participant.UserId;
        Person = people.Get(participant.UserId, participant.DisplayName);
        Name = participant.DisplayName;
        IsMe = participant.UserId == people.MyId;
        DisplayName = IsMe ? $"{participant.DisplayName} (you)" : participant.DisplayName;
        StatusText = participant.Status switch
        {
            CallParticipantStatus.Joined => "In the call",
            CallParticipantStatus.Ringing => "Calling…",
            CallParticipantStatus.Declined => "Declined",
            CallParticipantStatus.Missed => "Missed",
            CallParticipantStatus.Left => "Left",
            _ => participant.Status.ToString(),
        };
        IsJoined = participant.Status == CallParticipantStatus.Joined;
        IsRinging = participant.Status == CallParticipantStatus.Ringing;
    }

    public Guid UserId { get; }
    public PersonViewModel Person { get; }
    /// <summary>Clean name for avatars / initials (no “(you)” suffix).</summary>
    public string Name { get; }
    public string DisplayName { get; }
    public string StatusText { get; }
    public bool IsJoined { get; }
    public bool IsRinging { get; }
    public bool IsMe { get; }

    [ObservableProperty] private bool _isSpeaking;
    [ObservableProperty] private bool _isMicMuted;
}

public partial class CallHistoryItemViewModel : ObservableObject
{
    public CallHistoryItemViewModel(CallDto call, PeopleStore people)
    {
        Id = call.Id;
        ConversationId = call.ConversationId;
        Kind = call.Kind;
        Initiator = people.Get(call.Initiator);
        var peer = call.Participants.FirstOrDefault(p => p.UserId != people.MyId);
        Peer = peer is null
            ? Initiator
            : people.Get(peer.UserId, peer.DisplayName);
        IsActive = call.IsActive;
        Title = call.Kind == CallKind.Direct
            ? Peer.DisplayName
            : $"Group call ({call.Participants.Count})";
        TimeText = Describe.Time(call.CreatedAt);
        (StatusText, StatusKind) = DescribeStatus(call);
    }

    public Guid Id { get; }
    public Guid ConversationId { get; }
    public CallKind Kind { get; }
    public PersonViewModel Initiator { get; }
    /// <summary>Other party for direct calls; initiator for group history avatar.</summary>
    public PersonViewModel Peer { get; }
    public bool IsActive { get; }
    public string Title { get; }
    public string StatusText { get; }
    public string TimeText { get; }
    public CallHistoryStatusKind StatusKind { get; }

    public bool IsMissedStatus => StatusKind == CallHistoryStatusKind.Missed;
    public bool IsRejectedStatus => StatusKind == CallHistoryStatusKind.Rejected;
    public bool IsDurationStatus => StatusKind == CallHistoryStatusKind.Duration;

    private static (string Text, CallHistoryStatusKind Kind) DescribeStatus(CallDto call)
    {
        if (call.IsActive)
            return call.ConnectedAt is null ? ("Ringing…", CallHistoryStatusKind.Other) : ("In progress", CallHistoryStatusKind.Other);

        if (call.ConnectedAt is { } connected && call.EndedAt is { } ended)
        {
            var seconds = Math.Max(0, (int)(ended - connected).TotalSeconds);
            var text = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
            return (text, CallHistoryStatusKind.Duration);
        }

        if (call.Participants.Any(p => p.Status == CallParticipantStatus.Declined))
            return ("Rejected", CallHistoryStatusKind.Rejected);

        if (call.Participants.Any(p => p.Status == CallParticipantStatus.Missed))
            return ("Missed", CallHistoryStatusKind.Missed);

        return ("Ended", CallHistoryStatusKind.Other);
    }
}

/// <summary>Toast payload when a callee declines while the local user is still ringing out.</summary>
public sealed record CallDeclinedNotice(string PeerName, string Message);

/// <summary>Call history and the active ringing / in-call stage.</summary>
public partial class CallsViewModel : ViewModelBase
{
    /// <summary>Matches the server ring timeout so the caller hangs up if nobody answers.</summary>
    internal static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(30);

    private readonly SessionContext _context;
    private CancellationTokenSource? _ringTimeout;
    /// <summary>Prevents LoadAsync recovery from resurrecting a call we just left/ended.</summary>
    private Guid? _suppressRecoverCallId;
    private readonly HashSet<(Guid CallId, Guid UserId)> _notifiedDeclines = [];
    private readonly Dictionary<Guid, DateTimeOffset> _chatReadAt = [];

    public CallsViewModel(SessionContext context)
    {
        _context = context;
        context.Realtime.IncomingCall += OnIncomingCall;
        context.Realtime.CallUpdated += OnCallUpdated;
        context.Realtime.CallsChanged += OnCallsChanged;
        context.Realtime.CallMicMuted += OnCallMicMuted;
        context.Realtime.MessageReceived += OnConversationMessage;
        context.Realtime.ReadReceipt += OnReadReceipt;
    }

    public ObservableCollection<CallHistoryItemViewModel> History { get; } = [];
    public ObservableCollection<RoomChatMessageViewModel> ChatMessages { get; } = [];
    public ObservableCollection<PendingAttachmentViewModel> PendingAttachments { get; } = [];
    public IReadOnlyList<string> EmojiPalette => RoomChatMessageViewModel.EmojiPalette;

    [ObservableProperty] private bool _showEmojiPicker;
    [ObservableProperty] private bool _hasPendingAttachments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIncoming), nameof(IsInCall), nameof(ActivePeerName), nameof(ActivePeerPerson))]
    private CallDto? _incoming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInCall), nameof(HasMedia), nameof(IsRinging), nameof(CallStatusLabel))]
    private CallJoinDto? _activeJoin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(ActivePeerName), nameof(CallStatusLabel), nameof(IsRinging),
        nameof(RosterTitle), nameof(IsInCall))]
    private CallDto? _activeCall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMedia))]
    private Uri? _mediaPageUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePeerName))]
    private string _peerNameHint = "Call";

    /// <summary>Full immersive stage vs Teams-style floating bar while browsing.</summary>
    [ObservableProperty] private bool _isStageExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectingTiles))]
    private bool _showAddPeople;
    [ObservableProperty] private string? _addPeopleStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInviteSuggestions))]
    private string _inviteQuery = string.Empty;

    /// <summary>Right rail on the call stage: people roster vs in-call chat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatPanel))]
    private bool _showPeoplePanel = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnreadChat))]
    [NotifyPropertyChangedFor(nameof(UnreadChatBadge))]
    private int _unreadChatCount;

    [ObservableProperty] private string _chatDraft = string.Empty;

    [ObservableProperty] private bool _isCameraOn = true;
    [ObservableProperty] private bool _isMicOn = true;
    [ObservableProperty] private bool _isScreenShareOn;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecordingIndicator))]
    private bool _isRecording;

    /// <summary>True when the call initiator is recording and we are a remote participant.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecordingIndicator))]
    private bool _isRoomBeingRecorded;

    [ObservableProperty] private string _recordingElapsed = "00:00";
    [ObservableProperty] private string? _recordingNotice;

    public bool ShowRecordingIndicator => IsRecording || IsRoomBeingRecorded;

    private DispatcherTimer? _recordingUiTimer;
    private DateTimeOffset _recordingStartedAt;

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

    /// <summary>
    /// True once the LiveKit page reports it is connected and painting. Until then the
    /// Avalonia roster grid stays visible so the stage is never blank.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectingTiles))]
    private bool _isLiveKitSurfaceReady;

    /// <summary>
    /// Avalonia roster tiles while LiveKit is connecting, or while the native WebView is
    /// hidden so the Add people dialog can paint above it on macOS.
    /// </summary>
    public bool ShowConnectingTiles => ShowAddPeople || !IsLiveKitSurfaceReady;

    public ObservableCollection<PersonViewModel> InviteSuggestions { get; } = [];
    public ObservableCollection<CallRosterItemViewModel> Roster { get; } = [];

    public bool HasInviteSuggestions => InviteSuggestions.Count > 0;
    public bool ShowChatPanel => !ShowPeoplePanel;
    public bool HasUnreadChat => UnreadChatCount > 0;
    public string UnreadChatBadge => UnreadChatCount > 99 ? "99+" : UnreadChatCount.ToString();

    /// <summary>Wired by the shell to the LiveKit WebView host.</summary>
    public Func<Task>? ToggleCameraHandler { get; set; }
    public Func<Task>? ToggleMicHandler { get; set; }
    public Func<Task>? ToggleScreenShareHandler { get; set; }
    public Func<string, string, string, Task<bool>>? StartRecordingHandler { get; set; }
    public Func<Task<Guid?>>? StopRecordingHandler { get; set; }

    /// <summary>Only the call initiator can record.</summary>
    public bool CanRecord =>
        HasMedia
        && ActiveCall is { } call
        && call.Initiator.Id == _context.People.MyId;

    /// <summary>Raised so the call stage can scroll chat to the latest bubble (own send / open chat).</summary>
    public event Action? ChatFollowRequested;

    public void ApplyActiveSpeakers(IReadOnlyCollection<Guid> speakerIds)
    {
        var set = speakerIds as HashSet<Guid> ?? speakerIds.ToHashSet();
        foreach (var person in Roster)
            person.IsSpeaking = set.Contains(person.UserId);
    }

    /// <summary>Apply LiveKit mute badges for everyone in the call (local + remote).</summary>
    public void ApplyMutedParticipants(IReadOnlyCollection<Guid> mutedIds)
    {
        var set = mutedIds as HashSet<Guid> ?? mutedIds.ToHashSet();
        foreach (var person in Roster)
        {
            // Prefer LiveKit's mute list for remotes; keep toolbar state for "you".
            if (person.IsMe)
                person.IsMicMuted = !IsMicOn || set.Contains(person.UserId);
            else
                person.IsMicMuted = set.Contains(person.UserId);
        }
    }

    public void MarkLiveKitSurfaceReady() => IsLiveKitSurfaceReady = true;

    partial void OnIsMicOnChanged(bool value)
    {
        foreach (var person in Roster.Where(p => p.IsMe))
            person.IsMicMuted = !value;
        // Keep other participants' mute badges in sync via SignalR (LiveKit-only path was unreliable).
        _ = BroadcastMicMutedAsync();
    }

    partial void OnInviteQueryChanged(string value) => RefreshInviteSuggestions();

    public string RosterTitle =>
        Roster.Count switch
        {
            0 => "People",
            1 => "People · 1",
            var n => $"People · {n}",
        };

    /// <summary>Meet-style tile columns for the center participant grid.</summary>
    public int RosterGridColumns =>
        Roster.Count switch
        {
            <= 1 => 1,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4,
        };

    partial void OnActiveCallChanged(CallDto? value)
    {
        RefreshRoster();
        _ = LoadChatAsync();
        OnPropertyChanged(nameof(CanRecord));
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsStageExpandedChanged(bool value) => NotifyCallChrome();

    public bool IsEmpty => !IsLoading && History.Count == 0 && ErrorMessage is null;
    public bool HasIncoming => Incoming is not null && ActiveJoin is null;
    /// <summary>Local media session or a server-side active call recovered after restart.</summary>
    public bool IsInCall =>
        ActiveJoin is not null
        || (ActiveCall is { IsActive: true } && ActiveCall.Id != _suppressRecoverCallId);
    public bool HasMedia => MediaPageUri is not null && ActiveJoin is not null;
    public bool IsRinging => ActiveJoin is not null && ActiveCall?.ConnectedAt is null;

    public string ActivePeerName
    {
        get
        {
            if (ActiveCall is { } call) return PeerNameFrom(call);
            if (Incoming is { } incoming) return PeerNameFrom(incoming);
            return string.IsNullOrWhiteSpace(PeerNameHint) ? "Call" : PeerNameHint;
        }
    }

    /// <summary>Best-effort person for call chrome avatars (photo when known).</summary>
    public PersonViewModel? ActivePeerPerson =>
        Roster.FirstOrDefault(r => !r.IsMe)?.Person
        ?? Roster.FirstOrDefault()?.Person
        ?? (ActiveCall is { } call
            ? _context.People.Get(call.Initiator)
            : Incoming is { } incoming
                ? _context.People.Get(incoming.Initiator)
                : null);

    public string CallStatusLabel =>
        !IsInCall ? string.Empty
        : IsRinging ? "Calling…"
        : ShowSessionTimer
            ? (SessionWarningActive && !string.IsNullOrEmpty(SessionRemaining)
                ? $"{SessionElapsed} · {SessionRemaining}"
                : SessionElapsed)
            : "In progress";

    public event Action? IncomingChanged;

    /// <summary>Fired when media / expand / peer chrome changes so the shell can sync overlays.</summary>
    public event Action? CallChromeChanged;

    /// <summary>Caller-side: someone declined while we were still ringing.</summary>
    public event Action<CallDeclinedNotice>? CallDeclined;

    /// <summary>Opens the chat for a conversation (wired by the shell).</summary>
    public Func<Guid, Task>? OpenConversationHandler { get; set; }

    public void Detach()
    {
        CancelRingTimeout();
        _context.CallTones.Stop();
        _context.Realtime.IncomingCall -= OnIncomingCall;
        _context.Realtime.CallUpdated -= OnCallUpdated;
        _context.Realtime.CallsChanged -= OnCallsChanged;
        _context.Realtime.CallMicMuted -= OnCallMicMuted;
        _context.Realtime.MessageReceived -= OnConversationMessage;
        _context.Realtime.ReadReceipt -= OnReadReceipt;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var calls = await _context.Api.GetCallsAsync(_context.Organization.Id);
            History.Clear();
            CallDto? serverActive = null;
            foreach (var call in calls)
            {
                History.Add(new CallHistoryItemViewModel(call, _context.People));
                if (call.IsActive && serverActive is null) serverActive = call;
            }

            ReconcileActiveCall(serverActive);
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

    /// <summary>
    /// After restart the WebView join is gone but the server may still mark the call active —
    /// keep ActiveCall so Leave/End stays available and new calls are not blocked forever.
    /// </summary>
    private void ReconcileActiveCall(CallDto? serverActive)
    {
        if (serverActive is not null
            && (_suppressRecoverCallId == serverActive.Id || !serverActive.IsActive))
            serverActive = null;

        if (serverActive is null)
            _suppressRecoverCallId = null;

        if (ActiveJoin is { } join)
        {
            if (serverActive is null || serverActive.Id != join.CallId)
                ClearCallSession(keepPeerName: false);
            else
                ActiveCall = serverActive;
        }
        else if (serverActive is not null)
        {
            ActiveCall = serverActive;
            PeerNameHint = PeerNameFrom(serverActive);
        }
        else if (ActiveCall is not null || MediaPageUri is not null || IsStageExpanded)
        {
            ClearCallSession(keepPeerName: false);
        }

        NotifyCallChrome();
    }

    private void ClearCallSession(bool keepPeerName)
    {
        CancelRingTimeout();
        ActiveJoin = null;
        ActiveCall = null;
        MediaPageUri = null;
        IsLiveKitSurfaceReady = false;
        IsCameraOn = true;
        IsMicOn = true;
        IsScreenShareOn = false;
        IsRecording = false;
        StopRecordingUiTimer();
        StopSessionUiTimer();
        IsStageExpanded = false;
        ShowAddPeople = false;
        ShowPeoplePanel = true;
        UnreadChatCount = 0;
        ShowEmojiPicker = false;
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        _chatReadAt.Clear();
        PendingAttachments.Clear();
        HasPendingAttachments = false;
        ToggleCameraHandler = null;
        ToggleMicHandler = null;
        ToggleScreenShareHandler = null;
        StartRecordingHandler = null;
        StopRecordingHandler = null;
        if (!keepPeerName) PeerNameHint = "Call";
    }

    public async Task StartFromConversationAsync(Guid conversationId, string? peerName = null)
    {
        ErrorMessage = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(peerName)) PeerNameHint = peerName.Trim();
            var join = await _context.Api.StartCallAsync(_context.Organization.Id, conversationId);
            ActiveJoin = join;
            IsStageExpanded = true;
            await OpenMediaAsync(join);
            WatchRingTimeout(join.CallId);
            await LoadAsync();
            NotifyCallChrome();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private Task CallAgainAsync(CallHistoryItemViewModel item) =>
        StartFromConversationAsync(item.ConversationId, item.Title);

    [RelayCommand]
    private async Task EndCallAsync(CallHistoryItemViewModel item)
    {
        if (!item.IsActive) return;
        ErrorMessage = null;
        try
        {
            await _context.Api.EndCallAsync(item.Id);
            if (ActiveJoin?.CallId == item.Id || ActiveCall?.Id == item.Id)
            {
                _suppressRecoverCallId = item.Id;
                ClearCallSession(keepPeerName: false);
            }
            NotifyCallChrome();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private void ExpandStage()
    {
        if (!HasMedia) return;
        IsStageExpanded = true;
        NotifyCallChrome();
    }

    [RelayCommand]
    private void MinimizeStage()
    {
        if (!HasMedia) return;
        IsStageExpanded = false;
        NotifyCallChrome();
    }

    [RelayCommand]
    private void ShowPeople() => ShowPeoplePanel = true;

    [RelayCommand]
    private void ShowChat()
    {
        ShowPeoplePanel = false;
        UnreadChatCount = 0;
        RequestChatFollow();
        _ = MarkCallChatReadAsync();
    }

    partial void OnShowPeoplePanelChanged(bool value)
    {
        if (!value)
        {
            UnreadChatCount = 0;
            RequestChatFollow();
            _ = MarkCallChatReadAsync();
        }
    }

    private void RequestChatFollow() => ChatFollowRequested?.Invoke();

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
        // OnIsMicOnChanged broadcasts mute state to other participants via SignalR.
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

    /// <summary>Stop capture, upload to the call conversation, and post a chat attachment.</summary>
    public async Task FinalizeRecordingAsync()
    {
        // Leave/hang-up must not wait on the recorder when nothing is capturing —
        // StopRecordingAsync used to block up to 120s on a "recording-off" that never
        // completed the wait handle.
        if (!IsRecording) return;
        var conversationId = ActiveCall?.ConversationId;
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

        if (fileId is not { } id || conversationId is not { } convId)
        {
            RecordingNotice = "Recording stopped — file was not saved.";
            return;
        }

        try
        {
            await _context.Api.SendMessageAsync(
                new SendMessageRequest(convId, null, "Recording saved", [id], null, null));
            RecordingNotice = "Recording saved to this chat and Files.";
            RequestChatFollow();
        }
        catch (Exception ex)
        {
            // File is uploaded; chat announce is best-effort.
            RecordingNotice = Describe.Error(ex);
        }
    }

    private async Task BeginRecordingAsync()
    {
        if (StartRecordingHandler is null || ActiveCall is null) return;
        try
        {
            var token = await _context.Api.GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new ApiException("Sign in again to record.");

            var uploadUrl = new Uri(
                _context.Config.ApiBaseUri,
                $"api/conversations/{ActiveCall.ConversationId:D}/recording/upload").ToString();
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var fileName = $"call-{stamp}.webm";
            var started = await StartRecordingHandler.Invoke(uploadUrl, token, fileName);
            IsRecording = started;
            if (started)
            {
                RecordingNotice = "Recording started — participants notified";
                StartRecordingUiTimer(DateTimeOffset.Now);
            }
        }
        catch
        {
            IsRecording = false;
            StopRecordingUiTimer();
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
        if (IsRecording) return;

        IsRoomBeingRecorded = active;
        if (active)
        {
            var who = string.IsNullOrWhiteSpace(byName) ? "The host" : byName.Trim();
            RecordingNotice = $"{who} started recording this call";
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
        OnPropertyChanged(nameof(CallStatusLabel));
        if (_sessionUiTimer is not null) return;
        _sessionUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _sessionUiTimer.Tick += (_, _) => TickSessionUi();
        _sessionUiTimer.Start();
    }

    public void ApplySessionWarning()
    {
        SessionWarningActive = true;
        SessionNotice = "5 minutes remaining — call ends at 1 hour.";
        OnPropertyChanged(nameof(CallStatusLabel));
    }

    public bool ConsumeSessionExpired()
    {
        if (_sessionExpiryHandled) return false;
        _sessionExpiryHandled = true;
        SessionWarningActive = true;
        SessionNotice = "Time’s up — this call reached the 1 hour limit.";
        SessionRemaining = "00:00 left";
        OnPropertyChanged(nameof(CallStatusLabel));
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
        OnPropertyChanged(nameof(CallStatusLabel));
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
                SessionNotice = "Time’s up — this call reached the 1 hour limit.";
        }
        else
        {
            SessionRemaining = null;
        }

        OnPropertyChanged(nameof(CallStatusLabel));
    }

    private static string FormatClock(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");

    private async Task BroadcastMicMutedAsync()
    {
        if (ActiveCall is null) return;
        try
        {
            if (_context.Realtime.IsConnected)
                await _context.Realtime.SetCallMicMutedAsync(ActiveCall.Id, !IsMicOn);
        }
        catch
        {
            // Mute badge sync is best-effort; local chrome already updated.
        }
    }

    private void OnCallMicMuted(Guid callId, Guid userId, bool muted)
    {
        if (ActiveCall?.Id != callId && ActiveJoin?.CallId != callId) return;

        if (userId == _context.People.MyId)
        {
            // Another device / echo of our own broadcast — keep toolbar as source of truth.
            foreach (var person in Roster.Where(p => p.IsMe))
                person.IsMicMuted = !IsMicOn;
            return;
        }

        foreach (var person in Roster.Where(p => p.UserId == userId))
            person.IsMicMuted = muted;
    }

    private bool _permissionDialogOpen;

    public async Task NotifyMediaPermissionNeededAsync(string kind)
    {
        // Avoid stacking duplicate dialogs from connect + toggle races.
        if (_permissionDialogOpen) return;
        _permissionDialogOpen = true;
        try
        {
            switch (kind)
            {
                case "mic":
                    await _context.Platform.AlertAsync(
                        "Microphone access needed",
                        "MeetJoe is not allowed to use your microphone.\n\n1. Open System Settings → Privacy & Security → Microphone\n2. Turn on MeetJoe (or “dotnet” if listed while debugging)\n3. Leave this call and join again.");
                    break;
                case "camera":
                    await _context.Platform.AlertAsync(
                        "Camera access needed",
                        "MeetJoe is not allowed to use your camera, so video cannot turn on.\n\n1. Open System Settings → Privacy & Security → Camera\n2. Turn on MeetJoe\n3. Leave this call and join again.\n\nTip: launch MeetJoe.app (not a raw binary) so macOS can show the camera permission dialog.");
                    try { await _context.Platform.OpenUriAsync(new Uri("x-apple.systempreferences:com.apple.preference.security?Privacy_Camera")); }
                    catch { /* best-effort */ }
                    break;
                case "share":
                    await _context.Platform.AlertAsync(
                        "Screen sharing needed",
                        "MeetJoe could not share your screen.\n\n1. Open System Settings → Privacy & Security → Screen Recording\n2. Turn on MeetJoe\n3. Try Share screen again.\n\nTip: launch MeetJoe.app (not a raw binary) so macOS can show the screen-recording permission dialog.");
                    try { await _context.Platform.OpenUriAsync(new Uri("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")); }
                    catch { /* best-effort */ }
                    break;
                default:
                    await _context.Platform.AlertAsync(
                        "Microphone & camera access needed",
                        "MeetJoe needs permission to use your microphone and camera.\n\n1. Open System Settings → Privacy & Security\n2. Enable MeetJoe under Microphone and Camera\n3. Leave this call and join again.");
                    break;
            }
        }
        finally
        {
            _permissionDialogOpen = false;
        }
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
        if (ActiveCall is null) return;
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

    /// <summary>Upload files from Attach or drag-drop onto the call chat composer.</summary>
    public async Task AttachFilesAsync(IReadOnlyList<PickedFile> files)
    {
        if (ActiveCall is null || files.Count == 0) return;
        var conversationId = ActiveCall.ConversationId;
        try
        {
            foreach (var picked in files)
            {
                await using var content = await picked.OpenReadAsync();
                var file = await _context.Api.UploadFileAsync(conversationId, null, picked.Name, content);
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
        if (ActiveCall is null) return;
        if (string.IsNullOrWhiteSpace(ChatDraft) && PendingAttachments.Count == 0) return;

        var body = ChatDraft.Trim();
        var attachments = PendingAttachments.ToList();
        ChatDraft = string.Empty;
        PendingAttachments.Clear();
        HasPendingAttachments = false;
        ShowEmojiPicker = false;
        try
        {
            var request = new SendMessageRequest(
                ActiveCall.ConversationId, null, body, attachments.Select(a => a.FileId).ToList());
            var sent = _context.Realtime.IsConnected
                ? await _context.Realtime.SendMessageAsync(request)
                : await _context.Api.SendMessageAsync(request);
            RoomChatMessageViewModel.TryAdd(ChatMessages, sent, _context);
            RefreshChatSeenBy();
            RequestChatFollow();
        }
        catch
        {
            ChatDraft = body;
            foreach (var attachment in attachments) PendingAttachments.Add(attachment);
            HasPendingAttachments = PendingAttachments.Count > 0;
        }
    }

    [RelayCommand]
    private void BeginAddPeople()
    {
        if (!IsInCall) return;
        AddPeopleStatus = null;
        InviteQuery = string.Empty;
        RefreshInviteSuggestions();
        ShowAddPeople = true;
        NotifyCallChrome();
    }

    [RelayCommand]
    private void CancelAddPeople()
    {
        ShowAddPeople = false;
        AddPeopleStatus = null;
        InviteQuery = string.Empty;
        InviteSuggestions.Clear();
        NotifyCallChrome();
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
        var callId = ActiveJoin?.CallId ?? ActiveCall?.Id;
        if (callId is null) return;

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
            AddPeopleStatus = null;
            var updated = await _context.Api.InviteToCallAsync(callId.Value, [match.Id]);
            ActiveCall = updated;
            PeerNameHint = PeerNameFrom(updated);
            InviteQuery = string.Empty;
            ShowAddPeople = false;
            NotifyCallChrome();
            await LoadAsync();
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
        var onCall = (ActiveCall?.Participants ?? Incoming?.Participants ?? [])
            .Select(p => p.UserId)
            .Append(_context.People.MyId)
            .ToHashSet();
        return _context.People.People.Where(p =>
            !p.IsMe
            && !onCall.Contains(p.Id)
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

    private async Task LoadChatAsync()
    {
        ChatMessages.Clear();
        _chatReadAt.Clear();
        if (ActiveCall is null) return;
        try
        {
            var messages = await _context.Api.GetMessagesAsync(ActiveCall.ConversationId, null, null);
            foreach (var message in messages.OrderBy(m => m.CreatedAt))
                ChatMessages.Add(new RoomChatMessageViewModel(message, _context));
            RoomChatMessageViewModel.ApplyGrouping(ChatMessages);

            var conversations = await _context.Api.GetConversationsAsync(_context.Organization.Id);
            var conversation = conversations.FirstOrDefault(c => c.Id == ActiveCall.ConversationId);
            if (conversation?.ReadCursors is { } cursors)
            {
                foreach (var cursor in cursors)
                    _chatReadAt[cursor.UserId] = cursor.ReadAt;
            }

            RefreshChatSeenBy();
            RequestChatFollow();
            if (ShowChatPanel) await MarkCallChatReadAsync();
        }
        catch
        {
            // In-call chat is optional if history cannot load.
        }
    }

    private void OnConversationMessage(MessageDto message)
    {
        if (ActiveCall is null || message.ConversationId != ActiveCall.ConversationId) return;
        if (!RoomChatMessageViewModel.TryAdd(ChatMessages, message, _context)) return;
        RefreshChatSeenBy();
        if (message.Sender.Id == _context.People.MyId)
            RequestChatFollow();
        else if (!ShowChatPanel)
            UnreadChatCount++;
        else
            _ = MarkCallChatReadAsync();
    }

    private void OnReadReceipt(ReadReceiptDto receipt)
    {
        if (ActiveCall is null || receipt.ConversationId != ActiveCall.ConversationId) return;
        ApplyChatReadReceipt(receipt.UserId, receipt.ReadAt);
    }

    private async Task MarkCallChatReadAsync()
    {
        if (ActiveCall is null) return;
        try
        {
            await _context.Api.MarkConversationReadAsync(ActiveCall.ConversationId);
            ApplyChatReadReceipt(_context.People.MyId, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Best-effort.
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
        var peers = ActiveCall?.Participants.Select(p => (p.UserId, p.DisplayName))
                    ?? Enumerable.Empty<(Guid, string)>();
        RoomChatMessageViewModel.RefreshSeenBy(ChatMessages, _context.People.MyId, peers, _chatReadAt);
    }

    private void RefreshRoster()
    {
        Roster.Clear();
        if (ActiveCall is null)
        {
            OnPropertyChanged(nameof(RosterTitle));
            OnPropertyChanged(nameof(RosterGridColumns));
            OnPropertyChanged(nameof(ActivePeerPerson));
            OnPropertyChanged(nameof(ActivePeerName));
            return;
        }

        foreach (var participant in ActiveCall.Participants
                     .Where(p => p.Status is CallParticipantStatus.Joined or CallParticipantStatus.Ringing)
                     .OrderBy(p => p.Status == CallParticipantStatus.Joined ? 0 : 1)
                     .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new CallRosterItemViewModel(participant, _context.People);
            if (item.IsMe) item.IsMicMuted = !IsMicOn;
            Roster.Add(item);
        }

        OnPropertyChanged(nameof(RosterTitle));
        OnPropertyChanged(nameof(RosterGridColumns));
        OnPropertyChanged(nameof(ActivePeerPerson));
        OnPropertyChanged(nameof(ActivePeerName));
    }

    [RelayCommand]
    private async Task MessageAsync(CallHistoryItemViewModel item)
    {
        if (OpenConversationHandler is null) return;
        ErrorMessage = null;
        try
        {
            await OpenConversationHandler(item.ConversationId);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task AnswerAsync()
    {
        if (Incoming is null) return;
        ErrorMessage = null;
        try
        {
            var join = _context.Realtime.IsConnected
                ? await _context.Realtime.AnswerCallAsync(Incoming.Id)
                : await _context.Api.AnswerCallAsync(Incoming.Id);
            PeerNameHint = PeerNameFrom(Incoming);
            ActiveJoin = join;
            ActiveCall = Incoming;
            Incoming = null;
            IncomingChanged?.Invoke();
            IsStageExpanded = true;
            CancelRingTimeout();
            await OpenMediaAsync(join);
            await LoadAsync();
            NotifyCallChrome();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task DeclineAsync()
    {
        if (Incoming is null) return;
        try
        {
            if (_context.Realtime.IsConnected) await _context.Realtime.DeclineCallAsync(Incoming.Id);
            else await _context.Api.DeclineCallAsync(Incoming.Id);
            Incoming = null;
            IncomingChanged?.Invoke();
            await LoadAsync();
            NotifyCallChrome();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    [RelayCommand]
    private async Task HangUpAsync()
    {
        var callId = ActiveJoin?.CallId ?? ActiveCall?.Id;
        if (callId is null) return;
        _suppressRecoverCallId = callId;
        try { await FinalizeRecordingAsync(); }
        catch { /* best-effort before tear-down */ }
        ClearCallSession(keepPeerName: false);
        NotifyCallChrome();
        try
        {
            // End (not hang up) so a recovered orphaned session always clears the server lock.
            await _context.Api.EndCallAsync(callId.Value);
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }

        await LoadAsync();
    }

    private void WatchRingTimeout(Guid callId)
    {
        CancelRingTimeout();
        var cts = new CancellationTokenSource();
        _ringTimeout = cts;
        _ = EndIfUnansweredAsync(callId, cts.Token);
    }

    private void CancelRingTimeout()
    {
        try { _ringTimeout?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
        _ringTimeout?.Dispose();
        _ringTimeout = null;
    }

    private async Task EndIfUnansweredAsync(Guid callId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(RingTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ActiveJoin?.CallId != callId) return;
        if (ActiveCall?.ConnectedAt is not null) return;

        // Caller-side backup: end the ring so a new call can start without waiting for the server poll.
        await HangUpAsync();
    }

    private Task OpenMediaAsync(CallJoinDto join)
    {
        MediaPageUri = BuildMediaPageUri(_context.Config.ApiBaseUri, join);
        NotifyCallChrome();
        return Task.CompletedTask;
    }

    internal static Uri BuildMediaPageUri(Uri apiBase, CallJoinDto join)
    {
        // Query string (not hash): some embedded WebViews drop the URL fragment on navigate.
        // embed=1 hides the HTML toolbar — Avalonia owns call chrome.
        var page = new Uri(apiBase, "meet/");
        var query = "u=" + Uri.EscapeDataString(join.ServerUrl)
            + "&t=" + Uri.EscapeDataString(join.Token)
            + "&embed=1";
        return new UriBuilder(page) { Query = query }.Uri;
    }

    private string PeerNameFrom(CallDto call)
    {
        if (call.Kind == CallKind.Group)
            return $"Group call ({call.Participants.Count})";

        var other = call.Participants.FirstOrDefault(p => p.UserId != _context.People.MyId);
        return other?.DisplayName
            ?? (call.Initiator.Id == _context.People.MyId ? PeerNameHint : call.Initiator.DisplayName);
    }

    private void NotifyCallChrome()
    {
        OnPropertyChanged(nameof(ActivePeerName));
        OnPropertyChanged(nameof(CallStatusLabel));
        OnPropertyChanged(nameof(IsRinging));
        OnPropertyChanged(nameof(CanRecord));
        ToggleRecordingCommand.NotifyCanExecuteChanged();
        SyncCallTones();
        CallChromeChanged?.Invoke();
    }

    /// <summary>Incoming ringtone vs outgoing ringback — stops once connected or dismissed.</summary>
    private void SyncCallTones()
    {
        if (HasIncoming) _context.CallTones.PlayIncoming();
        else if (ActiveJoin is not null && ActiveCall?.ConnectedAt is null) _context.CallTones.PlayOutgoing();
        else _context.CallTones.Stop();
    }

    private void OnIncomingCall(CallDto call)
    {
        if (call.Initiator.Id == _context.People.MyId) return;
        Incoming = call;
        IncomingChanged?.Invoke();
        NotifyCallChrome();
    }

    private void OnCallUpdated(CallDto call)
    {
        if (Incoming?.Id == call.Id)
        {
            if (!call.IsActive) Incoming = null;
            else Incoming = call;
            IncomingChanged?.Invoke();
        }

        var isCurrent =
            ActiveJoin?.CallId == call.Id
            || ActiveCall?.Id == call.Id;

        if (isCurrent)
        {
            if (!call.IsActive)
            {
                MaybeNotifyCallDeclined(call);
                _suppressRecoverCallId = call.Id;
                ClearCallSession(keepPeerName: false);
            }
            else
            {
                ActiveCall = call;
                PeerNameHint = PeerNameFrom(call);
                if (call.ConnectedAt is not null) CancelRingTimeout();
                // Group calls can stay active after one person declines.
                MaybeNotifyCallDeclined(call, requireInactive: false);
            }
        }

        NotifyCallChrome();
        _ = LoadAsync();
    }

    private void MaybeNotifyCallDeclined(CallDto call, bool requireInactive = true)
    {
        // Only the ringing caller should see this — not the person who pressed Decline.
        if (call.Initiator.Id != _context.People.MyId) return;
        if (requireInactive && call.IsActive) return;
        // Already connected: hang-up / leave is not a decline toast.
        if (ActiveCall?.ConnectedAt is not null || call.ConnectedAt is not null) return;
        if (ActiveJoin is null && ActiveCall is null) return;

        var decliner = call.Participants.FirstOrDefault(p =>
            p.UserId != _context.People.MyId
            && p.Status == CallParticipantStatus.Declined
            && _notifiedDeclines.Add((call.Id, p.UserId)));
        if (decliner is null) return;

        var name = string.IsNullOrWhiteSpace(decliner.DisplayName) ? "Someone" : decliner.DisplayName;
        CallDeclined?.Invoke(new CallDeclinedNotice(
            name,
            $"{name} declined your call."));
    }

    private void OnCallsChanged() => _ = LoadAsync();
}
