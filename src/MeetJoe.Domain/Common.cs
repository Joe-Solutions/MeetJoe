using System.Security.Cryptography;

namespace MeetJoe.Domain;

/// <summary>Raised when a domain rule is violated. Surfaces to clients as a 400.</summary>
public sealed class DomainException(string message) : Exception(message);

public enum OrgRole { Owner = 0, Admin = 1, Member = 2, Guest = 3 }

public enum ConversationKind { Direct = 0, Group = 1 }

public enum ChannelKind { Standard = 0, Private = 1, Shared = 2 }

public enum WorkspaceRole { Owner = 0, Member = 1 }

public enum ChannelRole { Owner = 0, Member = 1 }

/// <summary>How noisy a conversation or channel should be for one member.</summary>
public enum NotifyMode { All = 0, Mentions = 1, None = 2 }

public enum InviteStatus { Pending = 0, Accepted = 1, Declined = 2 }

public enum RecurrenceKind { None = 0, Daily = 1, Weekly = 2 }

public enum RecordingStatus { Unavailable = 0, Starting = 1, Recording = 2, Ready = 3, Failed = 4 }

public enum CallKind { Direct = 0, Group = 1 }

public enum CallParticipantStatus { Ringing = 0, Joined = 1, Declined = 2, Missed = 3, Left = 4 }

/// <summary>Why an activity feed item was created for a recipient.</summary>
public enum ActivityKind { Reply = 0, Reaction = 1, Mention = 2, MeetingInvite = 3, MissedCall = 4 }

/// <summary>What the user has chosen to show while connected. Offline is derived, never stored.</summary>
public enum Availability { Available = 0, Away = 1, Busy = 2, DoNotDisturb = 3 }

internal static class Guard
{
    public static string Required(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new DomainException($"{name} is required.");
        if (trimmed.Length > maxLength) throw new DomainException($"{name} must be {maxLength} characters or fewer.");
        return trimmed;
    }

    public static string? Optional(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maxLength) throw new DomainException($"{name} must be {maxLength} characters or fewer.");
        return trimmed;
    }
}

/// <summary>Invite codes for joining an organization, e.g. <c>K7MQP-2XWHD</c>.</summary>
public static class JoinCodes
{
    // No 0/O, 1/I/L: codes get read aloud and typed by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 10;

    public static string Generate()
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, Length);
        return Format(new string(chars));
    }

    /// <summary>Canonical form of user input, or null when it cannot be a join code.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var compact = new string(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (compact.Length != Length || compact.Any(c => !Alphabet.Contains(c))) return null;
        return Format(compact);
    }

    private static string Format(string compact) => $"{compact[..5]}-{compact[5..]}";
}
