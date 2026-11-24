using MeetJoe.Application.Common;
using MeetJoe.Application.Presence;
using MeetJoe.Contracts;
using MeetJoe.Domain;
using Microsoft.EntityFrameworkCore;

namespace MeetJoe.Application.Profiles;

public sealed class ProfileService(
    IAppDbContext db,
    PresenceService presence,
    IRealtimePublisher realtime,
    IFileStorage storage)
{
    private static readonly HashSet<string> AllowedAvatarTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif",
    };

    public async Task<MeDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw AppException.NotFound("User");
        return ToDto(user);
    }

    public async Task<MeDto> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        user.UpdateProfile(request.DisplayName, request.StatusMessage);
        await db.SaveChangesAsync(ct);

        await presence.BroadcastAsync(userId, ct);
        await realtime.MembersChangedAsync(await db.GetColleagueIdsAsync(userId, ct), ct);
        return ToDto(user);
    }

    public async Task<MeDto> UploadAvatarAsync(
        Guid userId, string? fileName, string? contentType, long size, Stream content, CancellationToken ct = default)
    {
        if (size <= 0) throw AppException.Validation("Choose an image to upload.");
        if (size > User.MaxAvatarBytes)
            throw AppException.Validation("Profile photos must be 5 MB or smaller.");

        var type = NormalizeImageType(fileName, contentType);
        if (type is null)
            throw AppException.Validation("Use a JPEG, PNG, WebP, or GIF image.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        var key = User.AvatarObjectKey(userId);
        var previousKey = user.AvatarStorageKey;

        await storage.PutAsync(key, content, size, type, ct);
        user.SetAvatar(key, type);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }

        if (previousKey is not null && !string.Equals(previousKey, key, StringComparison.Ordinal))
        {
            try { await storage.DeleteAsync(previousKey, CancellationToken.None); }
            catch { /* best-effort */ }
        }

        await realtime.MembersChangedAsync(await db.GetColleagueIdsAsync(userId, ct), ct);
        return ToDto(user);
    }

    public async Task<MeDto> ClearAvatarAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        var key = user.AvatarStorageKey;
        user.ClearAvatar();
        await db.SaveChangesAsync(ct);
        if (key is not null)
        {
            try { await storage.DeleteAsync(key, CancellationToken.None); }
            catch { /* best-effort */ }
        }

        await realtime.MembersChangedAsync(await db.GetColleagueIdsAsync(userId, ct), ct);
        return ToDto(user);
    }

    /// <summary>Streams a colleague's avatar when the requester shares an organization with them.</summary>
    public async Task<(string ContentType, string StorageKey)> GetAvatarAsync(
        Guid requesterId, Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw AppException.NotFound("Avatar");
        if (user.AvatarStorageKey is null || user.AvatarContentType is null)
            throw AppException.NotFound("Avatar");

        if (requesterId != userId)
        {
            var sharesOrg = await db.Memberships.AnyAsync(
                m => m.UserId == requesterId
                     && db.Memberships.Any(o => o.OrganizationId == m.OrganizationId && o.UserId == userId),
                ct);
            if (!sharesOrg) throw AppException.NotFound("Avatar");
        }

        return (user.AvatarContentType, user.AvatarStorageKey);
    }

    private static MeDto ToDto(User user) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, user.Availability.ToStatus(), user.StatusMessage,
            Mapping.AvatarUrl(user.Id, user.AvatarStorageKey));

    private static string? NormalizeImageType(string? fileName, string? contentType)
    {
        var type = contentType?.Split(';')[0].Trim();
        if (type is not null && AllowedAvatarTypes.Contains(type))
            return type.Equals("image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : type.ToLowerInvariant();

        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null,
        };
    }
}
