using MeetJoe.Api.Common;
using MeetJoe.Application.Calls;
using MeetJoe.Application.Chat;
using MeetJoe.Application.Common;
using MeetJoe.Application.Meetings;
using MeetJoe.Application.Presence;
using MeetJoe.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MeetJoe.Api.Realtime;

[Authorize]
public sealed class ChatHub(ChatService chat) : Hub
{
    [HubMethodName(ChatHubMethods.SendMessage)]
    public Task<MessageDto> SendMessage(SendMessageRequest request) =>
        chat.SendAsync(Context.User.GetUserId(), request, Context.ConnectionAborted);

    [HubMethodName(ChatHubMethods.EditMessage)]
    public Task<MessageDto> EditMessage(Guid messageId, EditMessageRequest request) =>
        chat.EditAsync(Context.User.GetUserId(), messageId, request, Context.ConnectionAborted);

    [HubMethodName(ChatHubMethods.DeleteMessage)]
    public Task<MessageDto> DeleteMessage(Guid messageId) =>
        chat.DeleteAsync(Context.User.GetUserId(), messageId, Context.ConnectionAborted);

    [HubMethodName(ChatHubMethods.ToggleReaction)]
    public Task<MessageDto> ToggleReaction(Guid messageId, ToggleReactionRequest request) =>
        chat.ToggleReactionAsync(Context.User.GetUserId(), messageId, request, Context.ConnectionAborted);

    [HubMethodName(ChatHubMethods.SetTyping)]
    public Task SetTyping(Guid? conversationId, Guid? channelId, bool isTyping) =>
        chat.SetTypingAsync(Context.User.GetUserId(), conversationId, channelId, isTyping, Context.ConnectionAborted);
}

/// <summary>A user is online for as long as they hold at least one connection to this hub.</summary>
[Authorize]
public sealed class PresenceHub(PresenceService presence) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await presence.ConnectedAsync(Context.User.GetUserId(), Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await presence.DisconnectedAsync(Context.User.GetUserId(), Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    [HubMethodName(PresenceHubMethods.SetAvailability)]
    public Task<PresenceDto> SetAvailability(PresenceStatus availability) =>
        presence.SetAvailabilityAsync(Context.User.GetUserId(), availability, Context.ConnectionAborted);
}

/// <summary>Server-to-client only. See <see cref="NotificationHubEvents"/>.</summary>
[Authorize]
public sealed class NotificationHub : Hub;

[Authorize]
public sealed class MeetingHub(MeetingService meetings) : Hub
{
    [HubMethodName(MeetingHubMethods.RaiseHand)]
    public Task RaiseHand(Guid meetingId) =>
        meetings.RaiseHandAsync(Context.User.GetUserId(), meetingId, Context.ConnectionAborted);

    [HubMethodName(MeetingHubMethods.LowerHand)]
    public Task LowerHand(Guid meetingId) =>
        meetings.LowerHandAsync(Context.User.GetUserId(), meetingId, Context.ConnectionAborted);

    [HubMethodName(MeetingHubMethods.Admit)]
    public Task Admit(Guid meetingId, Guid userId) =>
        meetings.AdmitAsync(Context.User.GetUserId(), meetingId, userId, Context.ConnectionAborted);

    [HubMethodName(MeetingHubMethods.Reject)]
    public Task Reject(Guid meetingId, Guid userId) =>
        meetings.RejectAsync(Context.User.GetUserId(), meetingId, userId, Context.ConnectionAborted);

    [HubMethodName(MeetingHubMethods.RemoveParticipant)]
    public Task RemoveParticipant(Guid meetingId, Guid userId) =>
        meetings.RemoveParticipantAsync(Context.User.GetUserId(), meetingId, userId, Context.ConnectionAborted);

    [HubMethodName(MeetingHubMethods.MuteParticipant)]
    public Task MuteParticipant(Guid meetingId, Guid userId) =>
        meetings.MuteParticipantAsync(Context.User.GetUserId(), meetingId, userId, Context.ConnectionAborted);

    [HubMethodName(MeetingHubMethods.SendChat)]
    public Task<MessageDto> SendChat(Guid meetingId, string? body, IReadOnlyList<Guid>? attachmentIds = null) =>
        meetings.SendMeetingChatAsync(
            Context.User.GetUserId(), meetingId, body, Context.ConnectionAborted, attachmentIds);
}

[Authorize]
public sealed class CallHub(CallService calls) : Hub
{
    [HubMethodName(CallHubMethods.Answer)]
    public Task<CallJoinDto> Answer(Guid callId) =>
        calls.AnswerAsync(Context.User.GetUserId(), callId, Context.ConnectionAborted);

    [HubMethodName(CallHubMethods.Decline)]
    public Task Decline(Guid callId) =>
        calls.DeclineAsync(Context.User.GetUserId(), callId, Context.ConnectionAborted);

    [HubMethodName(CallHubMethods.HangUp)]
    public Task HangUp(Guid callId) =>
        calls.HangUpAsync(Context.User.GetUserId(), callId, Context.ConnectionAborted);

    [HubMethodName(CallHubMethods.SetMicMuted)]
    public Task SetMicMuted(Guid callId, bool muted) =>
        calls.SetMicMutedAsync(Context.User.GetUserId(), callId, muted, Context.ConnectionAborted);
}

internal sealed class SignalRRealtimePublisher(
    IHubContext<ChatHub> chat,
    IHubContext<PresenceHub> presence,
    IHubContext<NotificationHub> notifications,
    IHubContext<MeetingHub> meetings,
    IHubContext<CallHub> calls) : IRealtimePublisher
{
    public Task MessageCreatedAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default) =>
        chat.Clients.Users(Ids(userIds)).SendAsync(ChatHubEvents.MessageReceived, message, ct);

    public Task MessageUpdatedAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default) =>
        chat.Clients.Users(Ids(userIds)).SendAsync(ChatHubEvents.MessageUpdated, message, ct);

    public Task TypingAsync(IReadOnlyCollection<Guid> userIds, TypingDto typing, CancellationToken ct = default) =>
        chat.Clients.Users(Ids(userIds)).SendAsync(ChatHubEvents.Typing, typing, ct);

    public Task ReadReceiptAsync(IReadOnlyCollection<Guid> userIds, ReadReceiptDto receipt, CancellationToken ct = default) =>
        chat.Clients.Users(Ids(userIds)).SendAsync(ChatHubEvents.ReadReceipt, receipt, ct);

    public Task PresenceChangedAsync(IReadOnlyCollection<Guid> userIds, PresenceDto status, CancellationToken ct = default) =>
        presence.Clients.Users(Ids(userIds)).SendAsync(PresenceHubEvents.PresenceChanged, status, ct);

    public Task ConversationsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        NotifyAsync(userIds, NotificationHubEvents.ConversationsChanged, ct);

    public Task WorkspacesChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        NotifyAsync(userIds, NotificationHubEvents.WorkspacesChanged, ct);

    public Task MeetingsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        NotifyAsync(userIds, NotificationHubEvents.MeetingsChanged, ct);

    public Task MembersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        NotifyAsync(userIds, NotificationHubEvents.MembersChanged, ct);

    public Task ActivityChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        NotifyAsync(userIds, NotificationHubEvents.ActivityChanged, ct);

    public Task MeetingUpdatedAsync(IReadOnlyCollection<Guid> userIds, MeetingDto meeting, CancellationToken ct = default) =>
        meetings.Clients.Users(Ids(userIds)).SendAsync(MeetingHubEvents.MeetingUpdated, meeting, ct);

    public Task MeetingChatAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default) =>
        meetings.Clients.Users(Ids(userIds)).SendAsync(MeetingHubEvents.ChatMessage, message, ct);

    public Task ParticipantMutedAsync(
        IReadOnlyCollection<Guid> userIds, Guid meetingId, Guid targetUserId, CancellationToken ct = default) =>
        meetings.Clients.Users(Ids(userIds)).SendAsync(MeetingHubEvents.ParticipantMuted, meetingId, targetUserId, ct);

    public Task ParticipantRemovedAsync(
        IReadOnlyCollection<Guid> userIds, Guid meetingId, Guid targetUserId, CancellationToken ct = default) =>
        meetings.Clients.Users(Ids(userIds)).SendAsync(MeetingHubEvents.ParticipantRemoved, meetingId, targetUserId, ct);

    public Task HandRaisedAsync(
        IReadOnlyCollection<Guid> userIds, Guid meetingId, Guid userId, bool raised, CancellationToken ct = default) =>
        meetings.Clients.Users(Ids(userIds)).SendAsync(MeetingHubEvents.HandRaised, meetingId, userId, raised, ct);

    public Task IncomingCallAsync(IReadOnlyCollection<Guid> userIds, CallDto call, CancellationToken ct = default) =>
        calls.Clients.Users(Ids(userIds)).SendAsync(CallHubEvents.IncomingCall, call, ct);

    public Task CallUpdatedAsync(IReadOnlyCollection<Guid> userIds, CallDto call, CancellationToken ct = default) =>
        calls.Clients.Users(Ids(userIds)).SendAsync(CallHubEvents.CallUpdated, call, ct);

    public Task CallsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        NotifyAsync(userIds, NotificationHubEvents.CallsChanged, ct);

    public Task CallMicMutedAsync(
        IReadOnlyCollection<Guid> userIds, Guid callId, Guid userId, bool muted, CancellationToken ct = default) =>
        calls.Clients.Users(Ids(userIds)).SendAsync(CallHubEvents.MicMuted, callId, userId, muted, ct);

    private Task NotifyAsync(IReadOnlyCollection<Guid> userIds, string eventName, CancellationToken ct) =>
        notifications.Clients.Users(Ids(userIds)).SendAsync(eventName, ct);

    private static List<string> Ids(IReadOnlyCollection<Guid> userIds) => userIds.Select(id => id.ToString()).ToList();
}
