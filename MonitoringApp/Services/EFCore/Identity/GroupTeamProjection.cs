using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class GroupTeamProjection
{
    public static async Task ReconcileAsync(AlertDbContext context, Guid groupId, CancellationToken token)
    {
        var group = await context.ScimGroups.SingleAsync(group => group.Id == groupId, token);
        var mappings = await context.UserTeamGroupMappings.Where(mapping => mapping.GroupId == groupId).ToArrayAsync(token);
        var members = await ActiveMembers(context).Where(member => member.GroupId == groupId)
            .Select(member => member.UserId).ToArrayAsync(token);
        var rows = await context.UserTeamMemberships.Where(row => row.SourceGroupId == groupId && row.GroupMappingId != null).ToArrayAsync(token);
        var desired = mappings.Where(mapping => mapping.IsEnabled && group.IsActive && !group.IsDeleted)
            .SelectMany(mapping => members.Select(user => (Mapping: mapping.Id, User: user, Team: mapping.TeamId))).ToHashSet();
        context.UserTeamMemberships.RemoveRange(rows.Where(row => !desired.Contains((row.GroupMappingId!.Value, row.UserId, row.TeamId))));
        var existing = rows.Select(row => (Mapping: row.GroupMappingId!.Value, User: row.UserId, Team: row.TeamId)).ToHashSet();
        context.UserTeamMemberships.AddRange(desired.Except(existing).Select(key => new UserTeamMembership
        {
            GroupMappingId = key.Mapping, UserId = key.User, TeamId = key.Team, SourceGroupId = groupId
        }));
    }

    public static IQueryable<ScimGroupMember> ActiveMembers(AlertDbContext context) =>
        from member in context.ScimGroupMembers
        join state in context.ScimUserStates on member.UserId equals state.UserId
        where !member.IsDeleted && !state.IsDeleted
        select member;

    public static async Task RemoveUserReferencesAsync(AlertDbContext context, ProvisioningSource source, Guid userId, CancellationToken token)
    {
        var members = await context.ScimGroupMembers.Where(member =>
            member.ConnectionId == source.ConnectionId && member.UserId == userId && !member.IsDeleted).ToArrayAsync(token);
        var ids = members.Select(member => member.GroupId).ToArray();
        foreach (var member in members) member.IsDeleted = true;
        var groups = await context.ScimGroups.Where(group => ids.Contains(group.Id)).ToArrayAsync(token);
        foreach (var group in groups)
        {
            group.Revision++;
            group.LastSyncedAt = DateTimeOffset.UtcNow;
            group.SyncStatus = ProvisioningSyncStatuses.Succeeded;
        }
        context.UserTeamMemberships.RemoveRange(await context.UserTeamMemberships
            .Where(row => row.UserId == userId && row.GroupMappingId != null && row.SourceGroupId != null && ids.Contains(row.SourceGroupId.Value))
            .ToArrayAsync(token));
    }
}
