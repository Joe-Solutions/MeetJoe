using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetJoe.App.Services;
using MeetJoe.Contracts;

namespace MeetJoe.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

internal static class Describe
{
    /// <summary>A sentence the user can act on, for any failure of a server call.</summary>
    public static string Error(Exception exception) => exception switch
    {
        ApiException api => api.Message,
        HttpRequestException => "Can't reach the MeetJoe server. Check your connection and that the server is running.",
        TaskCanceledException => "The server took too long to respond. Try again.",
        InvalidOperationException invalid => invalid.Message,
        _ => "Something went wrong. Try again.",
    };

    public static string Time(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var local = value.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        if (local.Date == today) return local.ToString("h:mm tt");
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        return local.Year == today.Year ? local.ToString("MMM d") : local.ToString("MMM d, yyyy");
    }

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Presence(PresenceStatus status) => status switch
    {
        PresenceStatus.Available => "Available",
        PresenceStatus.Away => "Away",
        PresenceStatus.Busy => "Busy",
        PresenceStatus.DoNotDisturb => "Do not disturb",
        _ => "Offline",
    };
}

public partial class PersonViewModel(Guid id, string displayName, bool isMe) : ObservableObject
{
    public Guid Id { get; } = id;
    public bool IsMe { get; } = isMe;

    [ObservableProperty] private string _displayName = displayName;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private PresenceStatus _status = PresenceStatus.Offline;

    /// <summary>Relative API path (e.g. api/users/{id}/avatar), or null for initials.</summary>
    [ObservableProperty] private string? _avatarUrl;

    /// <summary>Decoded profile photo shared by every Avatar binding for this person.</summary>
    [ObservableProperty] private Bitmap? _photo;

    /// <summary>Used by pickers that let the user tick people.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>LiveKit active speaker — pulses the avatar ring in call/meeting tiles.</summary>
    [ObservableProperty] private bool _isSpeaking;

    /// <summary>True when this person's mic is muted in the LiveKit room.</summary>
    [ObservableProperty] private bool _isMicMuted;

    public string StatusText => Describe.Presence(Status);

    partial void OnAvatarUrlChanged(string? value) =>
        AvatarImages.RequestLoad(bitmap => Photo = bitmap, Id, value);

    public void ApplyAvatar(string? avatarUrl)
    {
        if (AvatarUrl == avatarUrl)
        {
            if (Photo is null && !string.IsNullOrWhiteSpace(avatarUrl))
                AvatarImages.RequestLoad(bitmap => Photo = bitmap, Id, avatarUrl);
            return;
        }

        AvatarUrl = avatarUrl;
    }
}

/// <summary>Everyone in the current organization, kept current from presence and membership events.</summary>
public sealed class PeopleStore(Guid myId)
{
    private readonly Dictionary<Guid, PersonViewModel> _byId = [];

    public Guid MyId { get; } = myId;

    /// <summary>Sorted by name.</summary>
    public ObservableCollection<PersonViewModel> People { get; } = [];

    public PersonViewModel Me => Get(MyId, "You");

    /// <summary>The shared instance for a user, created on first sight so bindings stay live.</summary>
    public PersonViewModel Get(Guid id, string displayName)
    {
        if (_byId.TryGetValue(id, out var person)) return person;

        person = new PersonViewModel(id, displayName, id == MyId);
        _byId[id] = person;
        Insert(person);
        return person;
    }

    public PersonViewModel Get(UserSummaryDto user)
    {
        var person = Get(user.Id, user.DisplayName);
        person.ApplyAvatar(user.AvatarUrl);
        return person;
    }

    public void Apply(IEnumerable<OrgMemberDto> members)
    {
        foreach (var member in members)
        {
            var person = Get(member.UserId, member.DisplayName);
            if (person.DisplayName != member.DisplayName)
            {
                person.DisplayName = member.DisplayName;
                People.Remove(person);
                Insert(person);
            }

            person.Email = member.Email;
            person.Status = member.Status;
            person.StatusMessage = member.StatusMessage;
            person.ApplyAvatar(member.AvatarUrl);
        }
    }

    public void Apply(PresenceDto presence)
    {
        if (!_byId.TryGetValue(presence.UserId, out var person)) return;
        person.Status = presence.Status;
        person.StatusMessage = presence.StatusMessage;
    }

    private void Insert(PersonViewModel person)
    {
        var index = 0;
        while (index < People.Count &&
               string.Compare(People[index].DisplayName, person.DisplayName, StringComparison.OrdinalIgnoreCase) <= 0)
            index++;
        People.Insert(index, person);
    }
}

/// <summary>What every signed-in view model needs.</summary>
public sealed record SessionContext(
    IMeetJoeApi Api,
    IRealtimeClient Realtime,
    IPlatformServices Platform,
    AppConfig Config,
    OrganizationDto Organization,
    PeopleStore People,
    ICallTonePlayer CallTones);
