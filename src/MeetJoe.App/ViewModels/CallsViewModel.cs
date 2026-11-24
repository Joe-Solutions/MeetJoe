using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
public sealed class CallRosterItemViewModel(CallParticipantDto participant, bool isMe)
{
    public Guid UserId { get; } = participant.UserId;
    public string DisplayName { get; } = isMe ? $"{participant.DisplayName} (you)" : participant.DisplayName;
    public string StatusText { get; } = participant.Status switch
    {
        CallParticipantStatus.Joined => "In the call",
        CallParticipantStatus.Ringing => "Calling…",
        CallParticipantStatus.Declined => "Declined",
        CallParticipantStatus.Missed => "Missed",
        CallParticipantStatus.Left => "Left",
        _ => participant.Status.ToString(),
    };
    public bool IsJoined { get; } = participant.Status == CallParticipantStatus.Joined;
    public bool IsRinging { get; } = participant.Status == CallParticipantStatus.Ringing;
}

public partial class CallHistoryItemViewModel : ObservableObject
{
    public CallHistoryItemViewModel(CallDto call, PeopleStore people)
    {
        Id = call.Id;
        ConversationId = call.ConversationId;
        Kind = call.Kind;
        Initiator = people.Get(call.Initiator);
        IsActive = call.IsActive;
        Title = call.Kind == CallKind.Direct
            ? (call.Participants.FirstOrDefault(p => p.UserId != people.MyId)?.DisplayName
               ?? call.Initiator.DisplayName)
            : $"Group call ({call.Participants.Count})";
        TimeText = Describe.Time(call.CreatedAt);
        (StatusText, StatusKind) = DescribeStatus(call);
    }

    public Guid Id { get; }
    public Guid ConversationId { get; }
    public CallKind Kind { get; }
    public PersonViewModel Initiator { get; }
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

/// <summary>Call history and the active ringing / in-call stage.</summary>
public partial class CallsViewModel : ViewModelBase
{
    /// <summary>Matches the server ring timeout so the caller hangs up if nobody answers.</summary>
    internal static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(30);

    private readonly SessionContext _context;
    private CancellationTokenSource? _ringTimeout;

    public CallsViewModel(SessionContext context)
    {
        _context = context;
        context.Realtime.IncomingCall += OnIncomingCall;
        context.Realtime.CallUpdated += OnCallUpdated;
        context.Realtime.CallsChanged += OnCallsChanged;
    }

    public ObservableCollection<CallHistoryItemViewModel> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIncoming), nameof(IsInCall), nameof(ActivePeerName))]
    private CallDto? _incoming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInCall), nameof(HasMedia))]
    private CallJoinDto? _activeJoin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePeerName), nameof(CallStatusLabel), nameof(IsRinging), nameof(RosterTitle))]
    private CallDto? _activeCall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMedia))]
    private Uri? _mediaPageUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePeerName))]
    private string _peerNameHint = "Call";

    /// <summary>Full immersive stage vs Teams-style floating bar while browsing.</summary>
    [ObservableProperty] private bool _isStageExpanded;

    [ObservableProperty] private bool _showAddPeople;
    [ObservableProperty] private string? _addPeopleStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInviteSuggestions))]
    private string _inviteQuery = string.Empty;

    /// <summary>Right rail on the call stage: people vs (reserved) chat-style list.</summary>
    [ObservableProperty] private bool _showPeoplePanel = true;

    public ObservableCollection<PersonViewModel> InviteSuggestions { get; } = [];
    public ObservableCollection<CallRosterItemViewModel> Roster { get; } = [];

    public bool HasInviteSuggestions => InviteSuggestions.Count > 0;

    partial void OnInviteQueryChanged(string value) => RefreshInviteSuggestions();

    public string RosterTitle =>
        Roster.Count switch
        {
            0 => "People",
            1 => "People · 1",
            var n => $"People · {n}",
        };

    partial void OnActiveCallChanged(CallDto? value) => RefreshRoster();

    partial void OnIsStageExpandedChanged(bool value) => NotifyCallChrome();

    public bool IsEmpty => !IsLoading && History.Count == 0 && ErrorMessage is null;
    public bool HasIncoming => Incoming is not null && ActiveJoin is null;
    /// <summary>Local media session or a server-side active call recovered after restart.</summary>
    public bool IsInCall => ActiveJoin is not null || ActiveCall is { IsActive: true };
    public bool HasMedia => MediaPageUri is not null && ActiveJoin is not null;
    public bool IsRinging => IsInCall && ActiveCall?.ConnectedAt is null;

    public string ActivePeerName
    {
        get
        {
            if (ActiveCall is { } call) return PeerNameFrom(call);
            if (Incoming is { } incoming) return PeerNameFrom(incoming);
            return string.IsNullOrWhiteSpace(PeerNameHint) ? "Call" : PeerNameHint;
        }
    }

    public string CallStatusLabel =>
        !IsInCall ? string.Empty
        : IsRinging ? "Calling…"
        : "In progress";

    public event Action? IncomingChanged;

    /// <summary>Fired when media / expand / peer chrome changes so the shell can sync overlays.</summary>
    public event Action? CallChromeChanged;

    /// <summary>Opens the chat for a conversation (wired by the shell).</summary>
    public Func<Guid, Task>? OpenConversationHandler { get; set; }

    public void Detach()
    {
        CancelRingTimeout();
        _context.CallTones.Stop();
        _context.Realtime.IncomingCall -= OnIncomingCall;
        _context.Realtime.CallUpdated -= OnCallUpdated;
        _context.Realtime.CallsChanged -= OnCallsChanged;
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
        if (ActiveJoin is { } join)
        {
            if (serverActive is null || serverActive.Id != join.CallId)
            {
                CancelRingTimeout();
                ActiveJoin = null;
                ActiveCall = null;
                MediaPageUri = null;
                IsStageExpanded = false;
            }
            else
            {
                ActiveCall = serverActive;
            }
        }
        else if (serverActive is not null)
        {
            ActiveCall = serverActive;
            PeerNameHint = PeerNameFrom(serverActive);
        }
        else if (ActiveCall is not null)
        {
            ActiveCall = null;
            IsStageExpanded = false;
        }

        NotifyCallChrome();
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
            if (ActiveJoin?.CallId == item.Id)
            {
                CancelRingTimeout();
                ActiveJoin = null;
                MediaPageUri = null;
                IsStageExpanded = false;
            }
            if (ActiveCall?.Id == item.Id) ActiveCall = null;
            PeerNameHint = "Call";
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

    private void RefreshRoster()
    {
        Roster.Clear();
        if (ActiveCall is null)
        {
            OnPropertyChanged(nameof(RosterTitle));
            return;
        }

        foreach (var participant in ActiveCall.Participants
                     .Where(p => p.Status is CallParticipantStatus.Joined or CallParticipantStatus.Ringing)
                     .OrderBy(p => p.Status == CallParticipantStatus.Joined ? 0 : 1)
                     .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            Roster.Add(new CallRosterItemViewModel(participant, participant.UserId == _context.People.MyId));
        }

        OnPropertyChanged(nameof(RosterTitle));
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
        CancelRingTimeout();
        ActiveJoin = null;
        ActiveCall = null;
        MediaPageUri = null;
        IsStageExpanded = false;
        ShowAddPeople = false;
        PeerNameHint = "Call";
        NotifyCallChrome();
        try
        {
            // End (not hang up) so a recovered orphaned session always clears the server lock.
            await _context.Api.EndCallAsync(callId.Value);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
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
        var page = new Uri(apiBase, "meet/");
        var query = "u=" + Uri.EscapeDataString(join.ServerUrl) + "&t=" + Uri.EscapeDataString(join.Token);
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

        if (ActiveJoin?.CallId == call.Id)
        {
            ActiveCall = call;
            PeerNameHint = PeerNameFrom(call);
            if (call.ConnectedAt is not null) CancelRingTimeout();
            if (!call.IsActive)
            {
                CancelRingTimeout();
                ActiveJoin = null;
                ActiveCall = null;
                MediaPageUri = null;
                IsStageExpanded = false;
            }
        }

        NotifyCallChrome();
        _ = LoadAsync();
    }

    private void OnCallsChanged() => _ = LoadAsync();
}
