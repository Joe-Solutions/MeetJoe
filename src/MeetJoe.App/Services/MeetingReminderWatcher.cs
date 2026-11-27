using MeetJoe.Contracts;

namespace MeetJoe.App.Services;

/// <summary>Pure rules for the 10-minute scheduled-meeting OS reminder.</summary>
public static class MeetingReminderRules
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True when <paramref name="now"/> is inside the pre-start window and this user
    /// is the host or an invitee who has not declined.
    /// </summary>
    public static bool ShouldNotify(
        MeetingDto meeting,
        Guid myUserId,
        DateTimeOffset now,
        ISet<Guid> alreadyNotified)
    {
        if (!meeting.IsActive || !meeting.IsScheduled) return false;
        if (alreadyNotified.Contains(meeting.Id)) return false;
        if (!IsInvitedOrHost(meeting, myUserId)) return false;

        var windowStart = meeting.StartsAt - LeadTime;
        return now >= windowStart && now < meeting.StartsAt;
    }

    public static bool IsInvitedOrHost(MeetingDto meeting, Guid myUserId)
    {
        if (meeting.Host.Id == myUserId) return true;
        return meeting.Invites.Any(i =>
            i.UserId == myUserId && i.Status != InviteStatus.Declined);
    }

    public static (string Title, string Body) Format(MeetingDto meeting, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((meeting.StartsAt - now).TotalMinutes));
        var when = meeting.StartsAt.ToLocalTime().ToString("h:mm tt");
        var title = "MeetJoe";
        var body = minutes <= 1
            ? $"“{meeting.Title}” starts now ({when})."
            : $"“{meeting.Title}” starts in {minutes} minutes ({when}).";
        return (title, body);
    }
}

/// <summary>
/// Polls the calendar and posts a native OS notification ~10 minutes before each
/// scheduled meeting this user was invited to (or is hosting).
/// </summary>
public sealed class MeetingReminderWatcher : IDisposable
{
    private readonly IMeetJoeApi _api;
    private readonly IOsNotificationService _notifications;
    private readonly Func<Guid> _organizationId;
    private readonly Func<Guid> _myUserId;
    private readonly TimeProvider _time;
    private readonly HashSet<Guid> _notified = [];
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MeetingReminderWatcher(
        IMeetJoeApi api,
        IOsNotificationService notifications,
        Func<Guid> organizationId,
        Func<Guid> myUserId,
        TimeProvider? time = null)
    {
        _api = api;
        _notifications = notifications;
        _organizationId = organizationId;
        _myUserId = myUserId;
        _time = time ?? TimeProvider.System;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _cts = new CancellationTokenSource();
            _loop = RunAsync(_cts.Token);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
        }

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* ignore */ }
        cts?.Dispose();
    }

    /// <summary>Visible for tests — one poll cycle.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var orgId = _organizationId();
        var myId = _myUserId();
        var now = _time.GetUtcNow();
        var from = now.AddMinutes(-5);
        var to = now.AddHours(36);

        IReadOnlyList<MeetingDto> calendar;
        try
        {
            calendar = await _api.GetCalendarAsync(orgId, from, to, ct);
        }
        catch
        {
            return;
        }

        foreach (var meeting in calendar)
        {
            if (!MeetingReminderRules.ShouldNotify(meeting, myId, now, _notified))
                continue;

            var (title, body) = MeetingReminderRules.Format(meeting, now);
            _notifications.Show(title, body);
            _notified.Add(meeting.Id);
        }

        // Drop ids for meetings that have started so a reschedule far in the future can notify again.
        _notified.RemoveWhere(id =>
        {
            var meeting = calendar.FirstOrDefault(m => m.Id == id);
            return meeting is null || meeting.StartsAt <= now;
        });
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // First check soon after sign-in so a meeting 8 minutes away still alerts.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Keep polling.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
