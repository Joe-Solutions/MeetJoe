using MeetJoe.Application.Common;
using MeetJoe.Application.Presence;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MeetJoe.Application.Profiles;

public sealed class ProfileService(IAppDbContext db, PresenceService presence, IRealtimePublisher realtime)
{
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

        // Colleagues hold this user's name and status message in their people lists.
        await presence.BroadcastAsync(userId, ct);
        await realtime.MembersChangedAsync(await db.GetColleagueIdsAsync(userId, ct), ct);
        return ToDto(user);
    }

    private static MeDto ToDto(Domain.User user) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, user.Availability.ToStatus(), user.StatusMessage);
}
