using MeetJoe.Application.Common;
using MeetJoe.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MeetJoe.Infrastructure.Identity;

public sealed class GuestAccountService(UserManager<User> users, TimeProvider time) : IGuestAccountService
{
    public async Task<User> GetOrCreateGuestAsync(string email, string displayName, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var existing = await users.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(displayName))
            {
                existing.UpdateProfile(displayName, existing.StatusMessage);
                await users.UpdateAsync(existing);
            }

            return existing;
        }

        var user = User.Create(normalized, displayName, time.GetUtcNow());
        // Guests never sign in with a password; Identity still requires a stored hash.
        var result = await users.CreateAsync(user, Guid.NewGuid().ToString("N") + "Aa1!");
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));

        return user;
    }
}
