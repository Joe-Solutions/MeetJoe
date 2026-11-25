using Microsoft.AspNetCore.Identity;

namespace MeetJoe.Domain;

public class User : IdentityUser<Guid>
{
    public const int MaxDisplayNameLength = 80;
    public const int MaxStatusMessageLength = 140;
    public const int MaxAvatarContentTypeLength = 100;
    public const long MaxAvatarBytes = 5 * 1024 * 1024;

    public string DisplayName { get; private set; } = string.Empty;
    public Availability Availability { get; private set; } = Availability.Available;
    public string? StatusMessage { get; private set; }
    /// <summary>MinIO object key for the profile photo, or null when using initials.</summary>
    public string? AvatarStorageKey { get; private set; }
    public string? AvatarContentType { get; private set; }
    /// <summary>Bumped on each photo change so clients can bust avatar caches.</summary>
    public DateTimeOffset? AvatarUpdatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static User Create(string email, string displayName, DateTimeOffset now)
    {
        var normalizedEmail = Guard.Required(email, "Email", 254);
        return new User
        {
            Id = Guid.CreateVersion7(),
            Email = normalizedEmail,
            UserName = normalizedEmail,
            DisplayName = Guard.Required(displayName, "Display name", MaxDisplayNameLength),
            CreatedAt = now,
        };
    }

    public void UpdateProfile(string displayName, string? statusMessage)
    {
        DisplayName = Guard.Required(displayName, "Display name", MaxDisplayNameLength);
        StatusMessage = Guard.Optional(statusMessage, "Status message", MaxStatusMessageLength);
    }

    public void SetAvatar(string storageKey, string contentType, DateTimeOffset now)
    {
        AvatarStorageKey = Guard.Required(storageKey, "Avatar storage key", 200);
        AvatarContentType = Guard.Required(contentType, "Avatar content type", MaxAvatarContentTypeLength);
        AvatarUpdatedAt = now;
    }

    public void ClearAvatar(DateTimeOffset now)
    {
        AvatarStorageKey = null;
        AvatarContentType = null;
        AvatarUpdatedAt = now;
    }

    public void SetAvailability(Availability availability) => Availability = availability;

    public static string AvatarObjectKey(Guid userId) => $"avatars/{userId:N}";
}

public class Organization
{
    public const int MaxNameLength = 80;

    private Organization() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string JoinCode { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public List<Membership> Memberships { get; private set; } = [];

    public static Organization Create(string name, Guid ownerId, DateTimeOffset now)
    {
        var org = new Organization
        {
            Id = Guid.CreateVersion7(),
            Name = Guard.Required(name, "Organization name", MaxNameLength),
            JoinCode = JoinCodes.Generate(),
            CreatedAt = now,
        };
        org.Memberships.Add(new Membership(org.Id, ownerId, OrgRole.Owner, now));
        return org;
    }

    /// <summary>Removes a member. The last owner must transfer ownership before leaving.</summary>
    public Membership Leave(Guid userId)
    {
        var membership = Memberships.FirstOrDefault(m => m.UserId == userId)
                         ?? throw new DomainException("You are not in this organization.");
        if (membership.Role == OrgRole.Owner && Memberships.Count(m => m.Role == OrgRole.Owner) == 1)
            throw new DomainException("Add another owner before leaving this organization.");

        Memberships.Remove(membership);
        return membership;
    }
}

public class Membership(Guid organizationId, Guid userId, OrgRole role, DateTimeOffset joinedAt)
{
    public Guid OrganizationId { get; private set; } = organizationId;
    public Guid UserId { get; private set; } = userId;
    public OrgRole Role { get; private set; } = role;
    public DateTimeOffset JoinedAt { get; private set; } = joinedAt;

    public Organization Organization { get; private set; } = null!;
    public User User { get; private set; } = null!;

    /// <summary>Owners and admins manage the organization (see invite code, later: policies).</summary>
    public bool CanManage => Role is OrgRole.Owner or OrgRole.Admin;
}
