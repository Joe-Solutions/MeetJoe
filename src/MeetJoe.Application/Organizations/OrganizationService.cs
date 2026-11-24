using MeetJoe.Application.Common;
using MeetJoe.Application.Presence;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Organizations;

public sealed class OrganizationService(
    IAppDbContext db, PresenceService presence, IRealtimePublisher realtime, TimeProvider time)
{
    public async Task<IReadOnlyList<OrganizationDto>> GetMineAsync(Guid userId, CancellationToken ct = default)
    {
        var memberships = await db.Memberships
            .Where(m => m.UserId == userId)
            .Include(m => m.Organization)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync(ct);
        return memberships.Select(ToDto).ToList();
    }

    public async Task<OrganizationDto> CreateAsync(Guid userId, CreateOrganizationRequest request, CancellationToken ct = default)
    {
        var organization = Domain.Organization.Create(request.Name, userId, time.GetUtcNow());
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        return ToDto(organization.Memberships[0], organization);
    }

    public async Task<OrganizationDto> JoinAsync(Guid userId, JoinOrganizationRequest request, CancellationToken ct = default)
    {
        var code = Domain.JoinCodes.Normalize(request.JoinCode)
                   ?? throw AppException.Validation("That invite code is not valid.");
        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.JoinCode == code, ct)
                           ?? throw AppException.Validation("That invite code is not valid.");

        var membership = await db.Memberships.FirstOrDefaultAsync(
            m => m.OrganizationId == organization.Id && m.UserId == userId, ct);
        if (membership is not null) return ToDto(membership, organization);

        membership = new Domain.Membership(organization.Id, userId, Domain.OrgRole.Member, time.GetUtcNow());
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);

        var memberIds = await db.Memberships
            .Where(m => m.OrganizationId == organization.Id)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        await realtime.MembersChangedAsync(memberIds, ct);
        return ToDto(membership, organization);
    }

    public async Task<IReadOnlyList<OrgMemberDto>> GetMembersAsync(Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var members = await db.Memberships
            .Where(m => m.OrganizationId == organizationId)
            .Select(m => new
            {
                m.UserId,
                m.User.DisplayName,
                m.User.Email,
                m.Role,
                m.User.Availability,
                m.User.StatusMessage,
                m.User.AvatarStorageKey,
            })
            .ToListAsync(ct);

        var statuses = await presence.ResolveAsync(members.Select(m => (m.UserId, m.Availability)).ToList());
        return members
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new OrgMemberDto(
                m.UserId,
                m.DisplayName,
                m.Email ?? string.Empty,
                m.Role.ToContract(),
                statuses[m.UserId],
                m.StatusMessage,
                Mapping.AvatarUrl(m.UserId, m.AvatarStorageKey)))
            .ToList();
    }

    public async Task LeaveAsync(Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        var organization = await db.Organizations
            .Include(o => o.Memberships)
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct)
            ?? throw AppException.NotFound("Organization");

        try
        {
            var removed = organization.Leave(userId);
            db.Memberships.Remove(removed);
        }
        catch (Domain.DomainException ex)
        {
            throw AppException.Validation(ex.Message);
        }

        await db.SaveChangesAsync(ct);

        var remaining = organization.Memberships.Select(m => m.UserId).ToList();
        if (remaining.Count > 0) await realtime.MembersChangedAsync(remaining, ct);
    }

    private static OrganizationDto ToDto(Domain.Membership membership) => ToDto(membership, membership.Organization);

    private static OrganizationDto ToDto(Domain.Membership membership, Domain.Organization organization) =>
        new(organization.Id, organization.Name, membership.Role.ToContract(),
            membership.CanManage ? organization.JoinCode : null);
}
