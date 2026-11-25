using MeetJoe.Application.Common;
using MeetJoe.Application.Presence;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Workspaces;

public sealed class WorkspaceService(
    IAppDbContext db, PresenceService presence, IRealtimePublisher realtime, TimeProvider time)
{
    public async Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(
        Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);
        return await LoadAsync(
            userId, db.WorkspaceMembers.Where(m => m.UserId == userId && m.Workspace.OrganizationId == organizationId), ct);
    }

    public async Task<WorkspaceDto> CreateAsync(
        Guid userId, Guid organizationId, CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var workspace = Domain.Workspace.Create(organizationId, userId, request.Name, request.Description, time.Now());
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);

        await realtime.WorkspacesChangedAsync([userId], ct);
        return await GetAsync(userId, workspace.Id, ct);
    }

    public async Task<IReadOnlyList<WorkspaceMemberDto>> GetMembersAsync(
        Guid userId, Guid workspaceId, CancellationToken ct = default)
    {
        await RequireMemberAsync(userId, workspaceId, ct);

        var members = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId)
            .Select(m => new { m.UserId, m.User.DisplayName, m.Role, m.User.Availability })
            .ToListAsync(ct);
        var statuses = await presence.ResolveAsync(members.Select(m => (m.UserId, m.Availability)).ToList());

        return members
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new WorkspaceMemberDto(m.UserId, m.DisplayName, m.Role.ToContract(), statuses[m.UserId]))
            .ToList();
    }

    public async Task AddMemberAsync(
        Guid userId, Guid workspaceId, AddWorkspaceMemberRequest request, CancellationToken ct = default)
    {
        var me = await RequireMemberAsync(userId, workspaceId, ct);
        if (me.Role != Domain.WorkspaceRole.Owner)
            throw AppException.Forbidden("Only workspace owners can add people.");

        var organizationId = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.OrganizationId).FirstAsync(ct);
        var inOrganization = await db.Memberships.AnyAsync(
            m => m.OrganizationId == organizationId && m.UserId == request.UserId, ct);
        if (!inOrganization) throw AppException.Validation("That person is not in this organization.");

        var alreadyMember = await db.WorkspaceMembers.AnyAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == request.UserId, ct);
        if (!alreadyMember)
        {
            db.WorkspaceMembers.Add(
                new Domain.WorkspaceMember(workspaceId, request.UserId, Domain.WorkspaceRole.Member, time.Now()));
            await db.SaveChangesAsync(ct);
        }

        await realtime.WorkspacesChangedAsync(await MemberIdsAsync(workspaceId, ct), ct);
    }

    public async Task<ChannelDto> CreateChannelAsync(
        Guid userId, Guid workspaceId, CreateChannelRequest request, CancellationToken ct = default)
    {
        await RequireMemberAsync(userId, workspaceId, ct);
        var now = time.Now();
        var kind = request.Kind.ToDomain();

        Domain.Channel channel;
        if (kind == Domain.ChannelKind.Private)
        {
            var organizationId = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.OrganizationId).FirstAsync(ct);
            var others = (request.MemberIds ?? []).Where(id => id != userId).Distinct().ToList();
            if (others.Count > 0)
            {
                var known = await db.WorkspaceMembers.CountAsync(
                    m => m.WorkspaceId == workspaceId && others.Contains(m.UserId), ct);
                if (known != others.Count)
                    throw AppException.Validation("Everyone in a private channel must belong to the workspace.");
                var inOrg = await db.Memberships.CountAsync(
                    m => m.OrganizationId == organizationId && others.Contains(m.UserId), ct);
                if (inOrg != others.Count)
                    throw AppException.Validation("Everyone in a private channel must belong to the organization.");
            }

            channel = Domain.Channel.CreatePrivate(workspaceId, userId, request.Name, request.Description, others, now);
        }
        else
        {
            channel = Domain.Channel.CreateStandard(workspaceId, request.Name, request.Description, now);
        }

        var taken = await db.Channels.AnyAsync(
            c => c.WorkspaceId == workspaceId && c.NormalizedName == channel.NormalizedName, ct);
        if (taken) throw AppException.Conflict($"This workspace already has a channel named “{channel.Name}”.");

        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);

        await realtime.WorkspacesChangedAsync(await MemberIdsAsync(workspaceId, ct), ct);
        return await ToDtoAsync(channel.Id, userId, ct);
    }

    public async Task<IReadOnlyList<ChannelMemberDto>> GetChannelMembersAsync(
        Guid userId, Guid channelId, CancellationToken ct = default)
    {
        var channel = await RequirePrivateChannelAccessAsync(userId, channelId, ct);
        var members = await db.ChannelMembers
            .Where(m => m.ChannelId == channel.Id)
            .Select(m => new { m.UserId, m.User.DisplayName, m.Role, m.User.Availability, m.LastReadAt })
            .ToListAsync(ct);
        var statuses = await presence.ResolveAsync(members.Select(m => (m.UserId, m.Availability)).ToList());

        return members
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new ChannelMemberDto(
                m.UserId, m.DisplayName, m.Role.ToContract(), statuses[m.UserId], m.LastReadAt))
            .ToList();
    }

    public async Task AddChannelMemberAsync(
        Guid userId, Guid channelId, AddChannelMemberRequest request, CancellationToken ct = default)
    {
        var channel = await RequirePrivateChannelOwnerAsync(userId, channelId, ct);
        var inWorkspace = await db.WorkspaceMembers.AnyAsync(
            m => m.WorkspaceId == channel.WorkspaceId && m.UserId == request.UserId, ct);
        if (!inWorkspace) throw AppException.Validation("That person is not in this workspace.");

        channel.AddMember(request.UserId, Domain.ChannelRole.Member, time.Now());
        await db.SaveChangesAsync(ct);
        await realtime.WorkspacesChangedAsync(await MemberIdsAsync(channel.WorkspaceId, ct), ct);
    }

    public async Task RemoveChannelMemberAsync(Guid userId, Guid channelId, Guid memberUserId, CancellationToken ct = default)
    {
        var channel = await RequirePrivateChannelOwnerAsync(userId, channelId, ct);
        channel.RemoveMember(memberUserId);
        await db.SaveChangesAsync(ct);
        await realtime.WorkspacesChangedAsync(await MemberIdsAsync(channel.WorkspaceId, ct), ct);
    }

    public async Task<ChannelDto> UpdateChannelPrefsAsync(
        Guid userId, Guid channelId, UpdateChannelPrefsRequest request, CancellationToken ct = default)
    {
        await db.ResolveTargetAsync(userId, null, channelId, ct);
        var member = await db.ChannelMembers.FirstOrDefaultAsync(
            m => m.ChannelId == channelId && m.UserId == userId, ct);
        if (member is null)
        {
            // Standard channels have no membership row; create a prefs-only row would require private.
            // For standard channels, prefs are only stored when a ChannelMember exists (private).
            throw AppException.Validation("Notification preferences are available on private channels.");
        }

        member.SetPreferences(request.IsMuted, request.NotifyMode.ToDomain());
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(channelId, userId, ct);
    }

    private async Task<Domain.Channel> RequirePrivateChannelAccessAsync(Guid userId, Guid channelId, CancellationToken ct)
    {
        var channel = await db.Channels
            .Include(c => c.Members)
            .Include(c => c.Workspace).ThenInclude(w => w.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
                      ?? throw AppException.NotFound("Channel");
        if (!channel.IsPrivate) throw AppException.Validation("Only private channels have a member list.");
        if (channel.Members.All(m => m.UserId != userId)) throw AppException.NotFound("Channel");
        return channel;
    }

    private async Task<Domain.Channel> RequirePrivateChannelOwnerAsync(Guid userId, Guid channelId, CancellationToken ct)
    {
        var channel = await RequirePrivateChannelAccessAsync(userId, channelId, ct);
        var me = channel.Members.First(m => m.UserId == userId);
        if (me.Role != Domain.ChannelRole.Owner)
            throw AppException.Forbidden("Only channel owners can change membership.");
        return channel;
    }

    private async Task<Domain.WorkspaceMember> RequireMemberAsync(Guid userId, Guid workspaceId, CancellationToken ct)
    {
        return await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct)
               ?? throw AppException.NotFound("Workspace");
    }

    private Task<List<Guid>> MemberIdsAsync(Guid workspaceId, CancellationToken ct) =>
        db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).Select(m => m.UserId).ToListAsync(ct);

    private async Task<WorkspaceDto> GetAsync(Guid userId, Guid workspaceId, CancellationToken ct)
    {
        var list = await LoadAsync(
            userId, db.WorkspaceMembers.Where(m => m.UserId == userId && m.WorkspaceId == workspaceId), ct);
        return list.FirstOrDefault() ?? throw AppException.NotFound("Workspace");
    }

    private async Task<List<WorkspaceDto>> LoadAsync(
        Guid userId, IQueryable<Domain.WorkspaceMember> mine, CancellationToken ct)
    {
        var rows = await mine
            .Select(m => new
            {
                m.Workspace.Id,
                m.Workspace.Name,
                m.Workspace.Description,
                m.Role,
                MemberCount = m.Workspace.Members.Count,
            })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var channels = await db.Channels.AsNoTracking()
            .Include(c => c.Members)
            .Where(c => ids.Contains(c.WorkspaceId))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var prefs = await db.ChannelMembers.AsNoTracking()
            .Where(m => m.UserId == userId && ids.Contains(m.Channel.WorkspaceId))
            .Select(m => new { m.ChannelId, m.IsMuted, m.NotifyMode })
            .ToListAsync(ct);
        var prefsByChannel = prefs.ToDictionary(p => p.ChannelId);

        var visible = channels.Where(c =>
            c.Kind != Domain.ChannelKind.Private || c.Members.Any(m => m.UserId == userId)).ToList();
        var channelsByWorkspace = visible.ToLookup(c => c.WorkspaceId);

        return rows
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new WorkspaceDto(
                r.Id, r.Name, r.Description, r.Role.ToContract(), r.MemberCount,
                channelsByWorkspace[r.Id].Select(c =>
                {
                    prefsByChannel.TryGetValue(c.Id, out var pref);
                    return new ChannelDto(
                        c.Id, c.WorkspaceId, c.Name, c.Description, c.Kind.ToContract(),
                        c.IsPrivate ? c.Members.Count : r.MemberCount,
                        pref?.IsMuted ?? false,
                        (pref?.NotifyMode ?? Domain.NotifyMode.All).ToContract());
                }).ToList()))
            .ToList();
    }

    private async Task<ChannelDto> ToDtoAsync(Guid channelId, Guid userId, CancellationToken ct)
    {
        var channel = await db.Channels.AsNoTracking()
                          .Include(c => c.Members)
                          .Include(c => c.Workspace).ThenInclude(w => w.Members)
                          .FirstOrDefaultAsync(c => c.Id == channelId, ct)
                      ?? throw AppException.NotFound("Channel");
        var pref = channel.Members.FirstOrDefault(m => m.UserId == userId);
        return new ChannelDto(
            channel.Id, channel.WorkspaceId, channel.Name, channel.Description, channel.Kind.ToContract(),
            channel.IsPrivate ? channel.Members.Count : channel.Workspace.Members.Count,
            pref?.IsMuted ?? false,
            (pref?.NotifyMode ?? Domain.NotifyMode.All).ToContract());
    }
}
