using System.Text.Json.Serialization;
using Avalonia.Threading;
using MeetJoe.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MeetJoe.App.Services;

/// <summary>One SignalR connection per hub, with automatic reconnect.</summary>
public sealed class RealtimeClient(AppConfig config, IAuthService auth) : IRealtimeClient, IAsyncDisposable
{
    private HubConnection? _chat;
    private HubConnection? _presence;
    private HubConnection? _notifications;
    private HubConnection? _meeting;
    private HubConnection? _call;
    private CancellationTokenSource? _session;

    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageDto>? MessageUpdated;
    public event Action<TypingDto>? Typing;
    public event Action<ReadReceiptDto>? ReadReceipt;
    public event Action<PresenceDto>? PresenceChanged;
    public event Action? ConversationsChanged;
    public event Action? WorkspacesChanged;
    public event Action? MeetingsChanged;
    public event Action? MembersChanged;
    public event Action? ActivityChanged;
    public event Action<MeetingDto>? MeetingUpdated;
    public event Action<MessageDto>? MeetingChat;
    public event Action<Guid, Guid>? ParticipantMuted;
    public event Action<Guid, Guid>? ParticipantRemoved;
    public event Action<Guid, Guid, bool>? HandRaised;
    public event Action<CallDto>? IncomingCall;
    public event Action<CallDto>? CallUpdated;
    public event Action? CallsChanged;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => Connections.All(c => c.State == HubConnectionState.Connected) && _chat is not null;

    private IEnumerable<HubConnection> Connections =>
        new[] { _chat, _presence, _notifications, _meeting, _call }.Where(c => c is not null)!;

    public async Task StartAsync()
    {
        await StopAsync();

        _chat = Build(HubPaths.Chat);
        _presence = Build(HubPaths.Presence);
        _notifications = Build(HubPaths.Notifications);
        _meeting = Build(HubPaths.Meeting);
        _call = Build(HubPaths.Call);

        _chat.On<MessageDto>(ChatHubEvents.MessageReceived, m => Post(() => MessageReceived?.Invoke(m)));
        _chat.On<MessageDto>(ChatHubEvents.MessageUpdated, m => Post(() => MessageUpdated?.Invoke(m)));
        _chat.On<TypingDto>(ChatHubEvents.Typing, t => Post(() => Typing?.Invoke(t)));
        _chat.On<ReadReceiptDto>(ChatHubEvents.ReadReceipt, r => Post(() => ReadReceipt?.Invoke(r)));
        _presence.On<PresenceDto>(PresenceHubEvents.PresenceChanged, p => Post(() => PresenceChanged?.Invoke(p)));
        _notifications.On(NotificationHubEvents.ConversationsChanged, () => Post(() => ConversationsChanged?.Invoke()));
        _notifications.On(NotificationHubEvents.WorkspacesChanged, () => Post(() => WorkspacesChanged?.Invoke()));
        _notifications.On(NotificationHubEvents.MeetingsChanged, () => Post(() => MeetingsChanged?.Invoke()));
        _notifications.On(NotificationHubEvents.MembersChanged, () => Post(() => MembersChanged?.Invoke()));
        _notifications.On(NotificationHubEvents.ActivityChanged, () => Post(() => ActivityChanged?.Invoke()));
        _notifications.On(NotificationHubEvents.CallsChanged, () => Post(() => CallsChanged?.Invoke()));
        _meeting.On<MeetingDto>(MeetingHubEvents.MeetingUpdated, m => Post(() => MeetingUpdated?.Invoke(m)));
        _meeting.On<MessageDto>(MeetingHubEvents.ChatMessage, m => Post(() => MeetingChat?.Invoke(m)));
        _meeting.On<Guid, Guid>(MeetingHubEvents.ParticipantMuted, (mid, uid) => Post(() => ParticipantMuted?.Invoke(mid, uid)));
        _meeting.On<Guid, Guid>(MeetingHubEvents.ParticipantRemoved, (mid, uid) => Post(() => ParticipantRemoved?.Invoke(mid, uid)));
        _meeting.On<Guid, Guid, bool>(MeetingHubEvents.HandRaised, (mid, uid, raised) => Post(() => HandRaised?.Invoke(mid, uid, raised)));
        _call.On<CallDto>(CallHubEvents.IncomingCall, c => Post(() => IncomingCall?.Invoke(c)));
        _call.On<CallDto>(CallHubEvents.CallUpdated, c => Post(() => CallUpdated?.Invoke(c)));

        foreach (var connection in Connections)
        {
            connection.Reconnecting += _ => ReportAsync();
            connection.Reconnected += _ => ReportAsync();
            connection.Closed += _ => ReportAsync();
        }

        _session = new CancellationTokenSource();
        await Task.WhenAll(Connections.Select(c => TryStartAsync(c, _session.Token)));
        await ReportAsync();
    }

    public async Task StopAsync()
    {
        _session?.Cancel();
        _session = null;
        var connections = Connections.ToList();
        _chat = _presence = _notifications = _meeting = _call = null;
        foreach (var connection in connections) await connection.DisposeAsync();
    }

    /// <summary>
    /// Automatic reconnect only covers connections that were established once, so a failed first
    /// attempt (server not up yet) is retried here in the background.
    /// </summary>
    private async Task TryStartAsync(HubConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.StartAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = RetryStartAsync(connection, ct);
        }
    }

    private async Task RetryStartAsync(HubConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                await connection.StartAsync(ct);
                await ReportAsync();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                // Still unreachable; try again.
            }
        }
    }

    public Task<MessageDto> SendMessageAsync(SendMessageRequest request) =>
        Require(_chat).InvokeAsync<MessageDto>(ChatHubMethods.SendMessage, request);

    public Task<MessageDto> EditMessageAsync(Guid messageId, string body) =>
        Require(_chat).InvokeAsync<MessageDto>(ChatHubMethods.EditMessage, messageId, new EditMessageRequest(body));

    public Task<MessageDto> DeleteMessageAsync(Guid messageId) =>
        Require(_chat).InvokeAsync<MessageDto>(ChatHubMethods.DeleteMessage, messageId);

    public Task<MessageDto> ToggleReactionAsync(Guid messageId, string emoji) =>
        Require(_chat).InvokeAsync<MessageDto>(ChatHubMethods.ToggleReaction, messageId, new ToggleReactionRequest(emoji));

    public Task SetTypingAsync(Guid? conversationId, Guid? channelId, bool isTyping) =>
        Require(_chat).SendAsync(ChatHubMethods.SetTyping, conversationId, channelId, isTyping);

    public Task<PresenceDto> SetAvailabilityAsync(PresenceStatus availability) =>
        Require(_presence).InvokeAsync<PresenceDto>(PresenceHubMethods.SetAvailability, availability);

    public Task RaiseHandAsync(Guid meetingId) =>
        Require(_meeting).SendAsync(MeetingHubMethods.RaiseHand, meetingId);

    public Task LowerHandAsync(Guid meetingId) =>
        Require(_meeting).SendAsync(MeetingHubMethods.LowerHand, meetingId);

    public Task AdmitAsync(Guid meetingId, Guid userId) =>
        Require(_meeting).SendAsync(MeetingHubMethods.Admit, meetingId, userId);

    public Task RejectAsync(Guid meetingId, Guid userId) =>
        Require(_meeting).SendAsync(MeetingHubMethods.Reject, meetingId, userId);

    public Task RemoveParticipantAsync(Guid meetingId, Guid userId) =>
        Require(_meeting).SendAsync(MeetingHubMethods.RemoveParticipant, meetingId, userId);

    public Task MuteParticipantAsync(Guid meetingId, Guid userId) =>
        Require(_meeting).SendAsync(MeetingHubMethods.MuteParticipant, meetingId, userId);

    public Task<MessageDto> SendMeetingChatAsync(Guid meetingId, string body) =>
        Require(_meeting).InvokeAsync<MessageDto>(MeetingHubMethods.SendChat, meetingId, body);

    public Task<CallJoinDto> AnswerCallAsync(Guid callId) =>
        Require(_call).InvokeAsync<CallJoinDto>(CallHubMethods.Answer, callId);

    public Task DeclineCallAsync(Guid callId) =>
        Require(_call).SendAsync(CallHubMethods.Decline, callId);

    public Task HangUpCallAsync(Guid callId) =>
        Require(_call).SendAsync(CallHubMethods.HangUp, callId);

    public async ValueTask DisposeAsync() => await StopAsync();

    private HubConnection Build(string path) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(config.ApiBaseUri, path), options => options.AccessTokenProvider = auth.GetAccessTokenAsync)
            .WithAutomaticReconnect(new KeepTryingPolicy())
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private Task ReportAsync()
    {
        var connected = IsConnected;
        Post(() => ConnectionChanged?.Invoke(connected));
        return Task.CompletedTask;
    }

    private static HubConnection Require(HubConnection? connection) =>
        connection is { State: HubConnectionState.Connected }
            ? connection
            : throw new InvalidOperationException("Not connected to the server.");

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>Back off quickly at first, then keep retrying for as long as the app is open.</summary>
    private sealed class KeepTryingPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < Delays.Length
                ? Delays[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(15);
    }
}
