using Microsoft.EntityFrameworkCore;
using static MonitoringApp.ProvisioningPersistence;

namespace MonitoringApp;

public sealed class ProvisioningUserStore(
    IDbContextFactory<AlertDbContext> factory, ILogger<ProvisioningUserStore> logger) : IProvisioningUserStore
{
    public async Task EnsureSourceAsync(ProvisioningSource source, CancellationToken cancellationToken) =>
        await ExecuteAsync(async context =>
        {
            var connection = await context.ScimConnections.SingleOrDefaultAsync(row => row.Id == source.ConnectionId, cancellationToken);
            if (connection is null)
            {
                context.ScimConnections.Add(new()
                {
                    Id = source.ConnectionId, TenantId = source.TenantId, Name = source.Name, IsEnabled = true
                });
                await context.SaveChangesAsync(cancellationToken);
            }
            else ValidateSource(connection, source);
            return true;
        }, cancellationToken);

    public Task<ProvisioningUser> GetAsync(ProvisioningSource source, Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, cancellationToken);
            var (user, state) = await FindAsync(context, source, id, false, cancellationToken);
            return Map(user, state);
        }, cancellationToken);

    public Task<ProvisioningPage> ListAsync(ProvisioningSource source, ProvisioningFilter? filter,
        int startIndex, int count, CancellationToken cancellationToken) => ExecuteAsync(async context =>
    {
        await RequireSourceAsync(context, source, cancellationToken);
        var query = from state in context.ScimUserStates
                    join user in context.DirectoryUsers on state.UserId equals user.Id
                    where state.ConnectionId == source.ConnectionId && state.TenantId == source.TenantId && !state.IsDeleted
                    select new { User = user, State = state };
        if (filter?.Attribute == "externalid")
        {
            var external = Guid.Parse(filter.Value);
            query = query.Where(row => row.State.ExternalId == external);
        }
        else if (filter?.Attribute == "username")
        {
            var name = filter.Value.ToUpperInvariant();
            query = query.Where(row => row.User.UserName.ToUpper() == name);
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = count == 0 ? [] : await query.OrderBy(row => row.User.Id)
            .Skip(startIndex - 1).Take(count).ToListAsync(cancellationToken);
        return new ProvisioningPage(total, rows.Select(row => Map(row.User, row.State)).ToArray());
    }, cancellationToken);

    public Task<ProvisioningUser> CreateAsync(ProvisioningSource source, ProvisioningProfile profile, CancellationToken cancellationToken) =>
        ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, cancellationToken);
            var identity = await context.EntraUserIdentities.SingleOrDefaultAsync(row =>
                row.TenantId == source.TenantId && row.ObjectId == profile.ExternalId, cancellationToken);
            var user = identity is null ? new DirectoryUser { Origin = DirectoryUserOrigins.Scim }
                : await context.DirectoryUsers.SingleAsync(row => row.Id == identity.UserId, cancellationToken);
            var state = await context.ScimUserStates.SingleOrDefaultAsync(row => row.UserId == user.Id, cancellationToken);
            if (state is not null && (state.ConnectionId != source.ConnectionId || !state.IsDeleted))
                throw Conflict();
            await RequireUniqueNameAsync(context, source, user.Id, profile.UserName, cancellationToken);
            if (identity is null)
            {
                context.DirectoryUsers.Add(user);
                context.EntraUserIdentities.Add(new() { UserId = user.Id, TenantId = source.TenantId, ObjectId = profile.ExternalId });
            }
            if (state is null)
            {
                state = new() { UserId = user.Id, ConnectionId = source.ConnectionId, TenantId = source.TenantId, ExternalId = profile.ExternalId };
                context.ScimUserStates.Add(state);
            }
            Update(user, state, profile);
            await context.SaveChangesAsync(cancellationToken);
            return Map(user, state);
        }, cancellationToken);

    public Task<ProvisioningUser> ReplaceAsync(ProvisioningSource source, Guid id, ProvisioningProfile profile,
        string? expectedVersion, CancellationToken cancellationToken) => ExecuteAsync(async context =>
    {
        await RequireSourceAsync(context, source, cancellationToken);
        var (user, state) = await FindAsync(context, source, id, false, cancellationToken);
        CheckVersion(Map(user, state), expectedVersion);
        if (state.ExternalId != profile.ExternalId)
            throw new ProvisioningException(ProvisioningError.Conflict, "The stable external identity cannot be changed.");
        await RequireUniqueNameAsync(context, source, id, profile.UserName, cancellationToken);
        Update(user, state, profile);
        await context.SaveChangesAsync(cancellationToken);
        return Map(user, state);
    }, cancellationToken);

    public async Task DeleteAsync(ProvisioningSource source, Guid id, string? expectedVersion, CancellationToken cancellationToken) =>
        await ExecuteAsync(async context =>
        {
            await RequireSourceAsync(context, source, cancellationToken);
            var (user, state) = await FindAsync(context, source, id, true, cancellationToken);
            CheckVersion(Map(user, state), expectedVersion);
            if (!state.IsDeleted)
            {
                state.IsDeleted = true;
                state.IsActive = false;
                state.Revision++;
                state.LastSyncedAt = DateTimeOffset.UtcNow;
                state.SyncStatus = ProvisioningSyncStatuses.Succeeded;
                await GroupTeamProjection.RemoveUserReferencesAsync(context, source, id, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }
            return true;
        }, cancellationToken);

    private static async Task<(DirectoryUser, ScimUserState)> FindAsync(
        AlertDbContext context, ProvisioningSource source, Guid id, bool includeDeleted, CancellationToken cancellationToken)
    {
        var state = await context.ScimUserStates.SingleOrDefaultAsync(row => row.UserId == id &&
            row.ConnectionId == source.ConnectionId && row.TenantId == source.TenantId &&
            (includeDeleted || !row.IsDeleted), cancellationToken)
            ?? throw new ProvisioningException(ProvisioningError.NotFound, "The SCIM user was not found.");
        var user = await context.DirectoryUsers.SingleAsync(row => row.Id == id, cancellationToken);
        return (user, state);
    }

    private static async Task RequireUniqueNameAsync(AlertDbContext context, ProvisioningSource source,
        Guid userId, string userName, CancellationToken cancellationToken)
    {
        var name = userName.ToUpperInvariant();
        if (await (from state in context.ScimUserStates
                   join user in context.DirectoryUsers on state.UserId equals user.Id
                   where state.ConnectionId == source.ConnectionId && !state.IsDeleted && user.Id != userId &&
                       user.UserName.ToUpper() == name
                   select user.Id).AnyAsync(cancellationToken))
            throw Conflict();
    }

    private static void Update(DirectoryUser user, ScimUserState state, ProvisioningProfile profile)
    {
        user.UserName = profile.UserName;
        user.DisplayName = profile.DisplayName;
        user.GivenName = profile.GivenName;
        user.FamilyName = profile.FamilyName;
        user.Email = profile.Email;
        user.Revision++;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        state.IsActive = profile.Active;
        state.IsDeleted = false;
        state.SyncStatus = ProvisioningSyncStatuses.Succeeded;
        state.LastSyncedAt = user.UpdatedAt;
        state.Revision++;
    }

    private static ProvisioningUser Map(DirectoryUser user, ScimUserState state) => new(user.Id,
        new(state.ExternalId, user.UserName, user.DisplayName, user.GivenName, user.FamilyName, user.Email, state.IsActive),
        user.CreatedAt, state.LastSyncedAt ?? user.UpdatedAt, user.Revision, state.Revision);

    private static void CheckVersion(ProvisioningUser user, string? expectedVersion)
    {
        if (expectedVersion is not null && !expectedVersion.Split(',').Any(value => value.Trim() is "*" || value.Trim() == user.Version))
            throw new ProvisioningException(ProvisioningError.Version, "The resource changed. Read it again before retrying.");
    }

    private static ProvisioningException Conflict() =>
        new(ProvisioningError.Conflict, "A user with this stable identity or userName already exists or belongs to another source.");

    private Task<T> ExecuteAsync<T>(Func<AlertDbContext, Task<T>> operation, CancellationToken cancellationToken) =>
        ProvisioningPersistence.ExecuteAsync(factory, logger, operation, cancellationToken, Conflict);
}
