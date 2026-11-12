using Microsoft.EntityFrameworkCore;
using static MonitoringApp.ProvisioningPersistence;

namespace MonitoringApp;

public sealed class ProvisioningGroupStore(IDbContextFactory<AlertDbContext> factory, ILogger<ProvisioningGroupStore> logger)
    : IProvisioningGroupStore
{
    public Task<ProvisioningGroup> GetAsync(ProvisioningSource source, Guid id, CancellationToken token) =>
        ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, token);
            return await MapAsync(context, await FindAsync(context, source, id, false, token), token);
        }, token);

    public Task<ProvisioningGroupPage> ListAsync(ProvisioningSource source, ProvisioningFilter? filter, int startIndex, int count, CancellationToken token) =>
        ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, token);
            var query = context.ScimGroups.Where(group => group.ConnectionId == source.ConnectionId && !group.IsDeleted);
            if (filter?.Attribute == "externalid")
            {
                var external = Guid.Parse(filter.Value);
                query = query.Where(group => group.ExternalId == external);
            }
            else if (filter?.Attribute == "displayname")
            {
                var name = filter.Value.ToUpperInvariant();
                query = query.Where(group => group.DisplayName.ToUpper() == name);
            }
            var total = await query.CountAsync(token);
            var groups = count == 0 ? [] : await query.OrderBy(group => group.Id).Skip(startIndex - 1).Take(count).ToArrayAsync(token);
            var ids = groups.Select(group => group.Id).ToArray();
            var members = (await GroupTeamProjection.ActiveMembers(context).Where(member => ids.Contains(member.GroupId)).ToArrayAsync(token))
                .ToLookup(member => member.GroupId, member => member.UserId);
            return new ProvisioningGroupPage(total, groups.Select(group => Map(group, members[group.Id].Order().ToArray())).ToArray());
        }, token);

    public Task<ProvisioningGroup> CreateAsync(ProvisioningSource source, ProvisioningGroupProfile profile, CancellationToken token) =>
        ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, token);
            var group = await context.ScimGroups.SingleOrDefaultAsync(group =>
                group.ConnectionId == source.ConnectionId && group.ExternalId == profile.ExternalId, token);
            if (group is not null && !group.IsDeleted) throw new ProvisioningException(ProvisioningError.Conflict, "This group already exists.");
            if (group is null)
            {
                group = new() { ConnectionId = source.ConnectionId, ExternalId = profile.ExternalId };
                context.ScimGroups.Add(group);
            }
            return await UpdateAsync(context, source, group, profile, token);
        }, token);

    public Task<ProvisioningGroup> ReplaceAsync(ProvisioningSource source, Guid id, ProvisioningGroupProfile profile, string version, CancellationToken token) =>
        ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, token);
            var group = await FindAsync(context, source, id, false, token);
            CheckVersion(group, version);
            if (profile.ExternalId != group.ExternalId) throw new ProvisioningException(ProvisioningError.Conflict, "The stable external identity cannot be changed.");
            return await UpdateAsync(context, source, group, profile, token);
        }, token);

    public async Task DeleteAsync(ProvisioningSource source, Guid id, string? version, CancellationToken token) =>
        await ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, token);
            var group = await FindAsync(context, source, id, true, token);
            CheckVersion(group, version);
            if (!group.IsDeleted)
            {
                group.IsDeleted = true;
                group.IsActive = false;
                Touch(group);
                foreach (var member in await context.ScimGroupMembers.Where(member => member.GroupId == id).ToArrayAsync(token))
                    member.IsDeleted = true;
                await context.SaveChangesAsync(token);
                await GroupTeamProjection.ReconcileAsync(context, id, token);
                await context.SaveChangesAsync(token);
            }
            return true;
        }, token);

    private static async Task<ProvisioningGroup> UpdateAsync(AlertDbContext context, ProvisioningSource source,
        ScimGroup group, ProvisioningGroupProfile profile, CancellationToken token)
    {
        var requested = profile.Members.ToHashSet();
        if (await context.ScimUserStates.CountAsync(state => requested.Contains(state.UserId) &&
            state.ConnectionId == source.ConnectionId && state.TenantId == source.TenantId && !state.IsDeleted, token) != requested.Count)
            throw new ProvisioningException(ProvisioningError.InvalidMember, "Every member must reference a provisioned User in this source.");
        var members = await context.ScimGroupMembers.Where(member => member.GroupId == group.Id).ToArrayAsync(token);
        foreach (var member in members) member.IsDeleted = !requested.Contains(member.UserId);
        var existing = members.Select(member => member.UserId).ToHashSet();
        context.ScimGroupMembers.AddRange(requested.Except(existing).Select(id =>
            new ScimGroupMember { GroupId = group.Id, ConnectionId = source.ConnectionId, UserId = id }));
        group.DisplayName = profile.DisplayName;
        group.IsDeleted = false;
        group.IsActive = true;
        Touch(group);
        await context.SaveChangesAsync(token);
        await GroupTeamProjection.ReconcileAsync(context, group.Id, token);
        await context.SaveChangesAsync(token);
        return Map(group, requested.Order().ToArray());
    }
    private static void Touch(ScimGroup group)
    {
        group.Revision++;
        group.LastSyncedAt = DateTimeOffset.UtcNow;
        group.SyncStatus = ProvisioningSyncStatuses.Succeeded;
    }
    private static async Task<ScimGroup> FindAsync(AlertDbContext context, ProvisioningSource source, Guid id, bool includeDeleted, CancellationToken token) =>
        await context.ScimGroups.SingleOrDefaultAsync(group => group.Id == id && group.ConnectionId == source.ConnectionId &&
            (includeDeleted || !group.IsDeleted), token) ?? throw new ProvisioningException(ProvisioningError.NotFound, "The SCIM group was not found.");
    private static async Task<ProvisioningGroup> MapAsync(AlertDbContext context, ScimGroup group, CancellationToken token) =>
        Map(group, (await GroupTeamProjection.ActiveMembers(context).Where(member => member.GroupId == group.Id)
            .Select(member => member.UserId).ToArrayAsync(token)).Order().ToArray());
    private static ProvisioningGroup Map(ScimGroup group, Guid[] members) =>
        new(group.Id, new(group.ExternalId, group.DisplayName, members), group.LastSyncedAt, group.Revision);
    private static void CheckVersion(ScimGroup group, string? version)
    {
        if (version is not null && !version.Split(',').Any(value => value.Trim() == "*" || value.Trim() == $"W/\"{group.Revision}\""))
            throw new ProvisioningException(ProvisioningError.Version, "The group changed. Read it again before retrying.");
    }
    private Task<T> ExecuteAsync<T>(Func<AlertDbContext, Task<T>> operation, CancellationToken token) =>
        ProvisioningPersistence.ExecuteAsync(factory, logger, operation, token);
}
