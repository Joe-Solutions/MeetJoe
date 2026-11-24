namespace MeetJoe.Contracts;

// Names shared by the SignalR hubs and their clients, so a rename is a compile error on both sides.

public static class HubPaths
{
    public const string Chat = "/hubs/chat";
    public const string Presence = "/hubs/presence";
    public const string Notifications = "/hubs/notifications";
    public const string Meeting = "/hubs/meeting";
    public const string Call = "/hubs/call";
}

public static class ChatHubMethods
{
    /// <summary>Client → server. Argument: <see cref="SendMessageRequest"/>. Returns <see cref="MessageDto"/>.</summary>
    public const string SendMessage = "SendMessage";

    /// <summary>Client → server. Arguments: message id, <see cref="EditMessageRequest"/>. Returns <see cref="MessageDto"/>.</summary>
    public const string EditMessage = "EditMessage";

    /// <summary>Client → server. Argument: message id. Returns <see cref="MessageDto"/>.</summary>
    public const string DeleteMessage = "DeleteMessage";

    /// <summary>Client → server. Arguments: message id, <see cref="ToggleReactionRequest"/>. Returns <see cref="MessageDto"/>.</summary>
    public const string ToggleReaction = "ToggleReaction";

    /// <summary>Client → server. Arguments: conversationId?, channelId?, isTyping.</summary>
    public const string SetTyping = "SetTyping";
}

public static class ChatHubEvents
{
    /// <summary>Server → client. Payload: <see cref="MessageDto"/>. Sent to every member of the conversation or channel.</summary>
    public const string MessageReceived = "MessageReceived";

    /// <summary>Server → client. Payload: <see cref="MessageDto"/> after edit, delete, or reaction change.</summary>
    public const string MessageUpdated = "MessageUpdated";

    /// <summary>Server → client. Payload: <see cref="TypingDto"/>.</summary>
    public const string Typing = "Typing";

    /// <summary>Server → client. Payload: <see cref="ReadReceiptDto"/>.</summary>
    public const string ReadReceipt = "ReadReceipt";
}

public static class PresenceHubMethods
{
    /// <summary>Client → server. Argument: <see cref="PresenceStatus"/> (not offline). Returns <see cref="PresenceDto"/>.</summary>
    public const string SetAvailability = "SetAvailability";
}

public static class PresenceHubEvents
{
    /// <summary>Server → client. Payload: <see cref="PresenceDto"/>.</summary>
    public const string PresenceChanged = "PresenceChanged";
}

public static class MeetingHubMethods
{
    public const string RaiseHand = "RaiseHand";
    public const string LowerHand = "LowerHand";
    public const string Admit = "Admit";
    public const string Reject = "Reject";
    public const string RemoveParticipant = "RemoveParticipant";
    public const string MuteParticipant = "MuteParticipant";
    public const string SendChat = "SendChat";
}

public static class MeetingHubEvents
{
    public const string MeetingUpdated = "MeetingUpdated";
    public const string HandRaised = "HandRaised";
    public const string ParticipantMuted = "ParticipantMuted";
    public const string ParticipantRemoved = "ParticipantRemoved";
    public const string ChatMessage = "ChatMessage";
}

/// <summary>
/// Server → client hints that a list the client holds is stale and should be fetched again.
/// They carry no data, so a client that misses one is corrected by its next fetch.
/// </summary>
public static class CallHubMethods
{
    public const string Answer = "Answer";
    public const string Decline = "Decline";
    public const string HangUp = "HangUp";
}

public static class CallHubEvents
{
    /// <summary>Server → client. Payload: <see cref="CallDto"/>. Incoming ring.</summary>
    public const string IncomingCall = "IncomingCall";

    /// <summary>Server → client. Payload: <see cref="CallDto"/>.</summary>
    public const string CallUpdated = "CallUpdated";
}

public static class NotificationHubEvents
{
    public const string ConversationsChanged = "ConversationsChanged";
    public const string WorkspacesChanged = "WorkspacesChanged";
    public const string MeetingsChanged = "MeetingsChanged";
    public const string MembersChanged = "MembersChanged";
    public const string ActivityChanged = "ActivityChanged";
    public const string CallsChanged = "CallsChanged";
}

public static class AuthConstants
{
    public const string TokenEndpoint = "/connect/token";
    public const string DesktopClientId = "meetjoe-desktop";
}
