using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class UserAdministrationService(
    IDbContextFactory<AlertDbContext> factory,
    IdentityDirectoryService directory,
    ApplicationAuthenticationOptions authentication,
    SqlAuthenticationService sql,
    ILogger<UserAdministrationService> logger)
{
    public Task<UserAdministrationSnapshot> GetSnapshotAsync(ClaimsPrincipal administrator, CancellationToken cancellationToken = default) =>
        ExecuteAsync(administrator, ReadSnapshotAsync, cancellationToken);

    public Task CreateSqlUserAsync(ClaimsPrincipal administrator, string username, string password, string role,
        SqlUserProfile profile, string? localDisplayName, IReadOnlyList<Guid> manualTeamIds, CancellationToken cancellationToken = default) =>
        ExecuteAsync(administrator, async (context, actor, token) =>
        {
            var name = await sql.CreateInContextAsync(context, username, password, role, token);
            if (await context.LegacyUserLinks.AnyAsync(link =>
                link.Status == LegacyUserLinkStatuses.Confirmed && link.Alias == name, token))
                throw Error("ReservedLogin");
            var target = await ReadTargetAsync(context, "sql:" + name, token);
            var user = BindSqlUser(context, target);
            ApplyProfile(user, profile);
            user.LocalDisplayName = OptionalText(localDisplayName, 256);
            await SetManualTeamsAsync(context, user.Id, manualTeamIds, token);
            await context.SaveChangesAsync(token);
            var after = await ReadTargetAsync(context, "sql:" + name, token);
            Audit(context, actor, user.Id, null, "SqlUserCreated", null, AuditUser(after));
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken);

    public Task SaveUserAsync(ClaimsPrincipal administrator, UserAdministrationEdit edit, CancellationToken cancellationToken = default) =>
        ExecuteAsync(administrator, async (context, actor, token) =>
        {
            var target = await ReadTargetAsync(context, edit.Key, token);
            RequireVersion(target, edit.Version);
            if (target.IsHistorical && target.AliasLinks.Any(link => link.AliasUserId == target.User?.Id))
                throw Error("HistoricalGrouped");
            var before = AuditUser(target);
            if ((!target.CanManageSql && (edit.SqlRole is not null || edit.NewPassword is not null)) ||
                (!target.CanManageSql && !target.IsHistorical && edit.Profile is not null) ||
                (target.IsHistorical && (edit.IsEnabled != (target.User?.IsEnabled ?? true) ||
                    !edit.ManualTeamIds.ToHashSet().SetEquals(target.Memberships.Where(membership => membership.GroupMappingId is null)
                        .Select(membership => membership.TeamId)))))
                throw Error("SourceReadOnly");
            if (!target.IsHistorical && edit.HistoricalAliasKeys is not null)
                throw Error("SourceReadOnly");
            if (actor.HasValue && target.User?.Id == actor.Value && !edit.IsEnabled)
                throw Error("SelfDisable");
            if (!edit.IsEnabled || edit.SqlRole is not null && edit.SqlRole != SqlAuthenticationRoles.Admin)
                await RequireOtherSqlAdminAsync(context, target, token);

            var user = target.User ?? (target.IsHistorical
                ? AddHistoricalProfile(context, target.HistoricalAlias!) : BindSqlUser(context, target));
            if (edit.Profile is not null) ApplyProfile(user, edit.Profile);
            if (target.IsHistorical && edit.Profile is not null && string.IsNullOrWhiteSpace(edit.Profile.DisplayName))
                user.DisplayName = "";
            user.LocalDisplayName = OptionalText(edit.LocalDisplayName, 256);
            user.IsEnabled = edit.IsEnabled;
            user.Revision++;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await SetManualTeamsAsync(context, user.Id, edit.ManualTeamIds, token);
            if (edit.SqlRole is not null && edit.SqlRole != target.Credential!.Role)
            {
                await sql.SetRoleInContextAsync(context, target.Credential.Username, edit.SqlRole, token);
                await context.Entry(target.Credential).ReloadAsync(token);
            }
            if (!string.IsNullOrEmpty(edit.NewPassword))
                await sql.SetPasswordInContextAsync(context, target.Credential!.Username, edit.NewPassword, token);
            if (edit.HistoricalAliasKeys is not null)
                await SetHistoricalAliasesAsync(context, target, user, edit.HistoricalAliasKeys, token);
            await context.SaveChangesAsync(token);
            var after = await ReadTargetAsync(context, edit.Key, token);
            Audit(context, actor, user.Id, null, target.IsHistorical ? "HistoricalProfileUpdated" :
                string.IsNullOrEmpty(edit.NewPassword) ? "UserUpdated" : "UserUpdatedWithPasswordReset", before, AuditUser(after));
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken);

    public Task DeleteSqlUserAsync(ClaimsPrincipal administrator, string key, string version, CancellationToken cancellationToken = default) =>
        ExecuteAsync(administrator, async (context, actor, token) =>
        {
            var target = await ReadTargetAsync(context, key, token);
            RequireVersion(target, version);
            if (!target.CanManageSql) throw Error("SourceReadOnly");
            await RequireOtherSqlAdminAsync(context, target, token);
            var before = AuditUser(target);
            var user = target.User ?? BindSqlUser(context, target);
            await context.SaveChangesAsync(token);
            if (!await sql.DeleteCoreAsync(context, target.Credential!.Username, token))
                throw Error("Missing");
            Audit(context, actor, user.Id, null, "SqlCredentialDeleted", before, new { user.Id, IsEnabled = false });
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken);

    public Task SaveTeamAsync(ClaimsPrincipal administrator, Guid? id, int? revision, string name, bool enabled,
        CancellationToken cancellationToken = default) =>
        SaveTeamAsync(administrator, id, revision, name, enabled, null, cancellationToken);

    public Task SaveTeamAsync(ClaimsPrincipal administrator, Guid? id, int? revision, string name, bool enabled,
        IReadOnlyList<string>? inventoryRoles, CancellationToken cancellationToken = default) =>
        ExecuteAsync(administrator, async (context, actor, token) =>
        {
            var normalizedName = name.Trim();
            if (normalizedName.Length is 0 or > 256) throw Error("TeamName");
            if (await context.UserTeams.AnyAsync(team => team.Id != id && team.Name.ToUpper() == normalizedName.ToUpper(), token))
                throw Error("DuplicateTeam");
            UserTeam team;
            object? before = null;
            if (id is null)
            {
                team = new() { Name = normalizedName, IsEnabled = enabled };
                context.UserTeams.Add(team);
            }
            else
            {
                team = await context.UserTeams.SingleOrDefaultAsync(team => team.Id == id, token) ?? throw Error("Missing");
                if (team.Revision != revision) throw Error("Stale");
                before = new { team.Id, team.Name, team.IsEnabled, team.Revision,
                    InventoryRoleKeys = await TeamRoleKeysAsync(context, team.Id, token) };
                team.Name = normalizedName;
                team.IsEnabled = enabled;
                team.Revision++;
            }
            if (inventoryRoles is not null)
                await SetInventoryRolesAsync(context, team.Id, inventoryRoles, token);
            await context.SaveChangesAsync(token);
            Audit(context, actor, null, team.Id, id is null ? "TeamCreated" : "TeamUpdated",
                before, new { team.Id, team.Name, team.IsEnabled, team.Revision,
                    InventoryRoleKeys = await TeamRoleKeysAsync(context, team.Id, token) });
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken);

    public Task DetachHistoricalAliasAsync(ClaimsPrincipal administrator, string key, string version,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(administrator, async (context, actor, token) =>
        {
            var target = await ReadTargetAsync(context, key, token);
            RequireVersion(target, version);
            if (!target.IsHistorical) throw Error("SourceReadOnly");
            var link = target.AliasLinks.SingleOrDefault(link => link.AliasUserId == target.User?.Id)
                ?? throw Error("HistoricalAliases");
            var before = AuditUser(target);
            var main = await context.DirectoryUsers.SingleAsync(user => user.Id == link.MainUserId, token);
            context.HistoricalUserAliases.Remove(link);
            TouchProfile(main);
            TouchProfile(target.User!);
            await context.SaveChangesAsync(token);
            Audit(context, actor, target.User!.Id, null, "HistoricalAliasDetached", before,
                AuditUser(await ReadTargetAsync(context, key, token)));
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken);

    public Task SaveGroupMappingAsync(ClaimsPrincipal administrator, Guid? id, int? revision, Guid teamId, Guid groupId, bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(administrator, async (context, actor, token) =>
        {
            UserTeamGroupMapping mapping;
            UserAdministrationMapping? before = null;
            if (id is null)
            {
                if (await context.UserTeamGroupMappings.AnyAsync(row => row.TeamId == teamId && row.GroupId == groupId, token))
                    throw Error("DuplicateMapping");
                mapping = new() { TeamId = teamId, GroupId = groupId, IsEnabled = enabled };
                context.UserTeamGroupMappings.Add(mapping);
            }
            else
            {
                mapping = await context.UserTeamGroupMappings.SingleOrDefaultAsync(row => row.Id == id, token) ?? throw Error("Missing");
                if (mapping.Revision != revision) throw Error("Stale");
                if (mapping.GroupId != groupId || mapping.TeamId != teamId) throw Error("MappingIdentity");
                before = Mapping(mapping);
                mapping.IsEnabled = enabled;
                mapping.Revision++;
            }
            var group = await context.ScimGroups.SingleOrDefaultAsync(row => row.Id == groupId, token) ?? throw Error("Missing");
            var team = await context.UserTeams.SingleOrDefaultAsync(row => row.Id == teamId, token) ?? throw Error("Missing");
            if (enabled && (group.IsDeleted || !group.IsActive || !team.IsEnabled && before?.IsEnabled != true))
                throw Error("MappingSource");
            await context.SaveChangesAsync(token);
            await GroupTeamProjection.ReconcileAsync(context, groupId, token);
            Audit(context, actor, null, teamId, id is null ? "GroupMappingCreated" : "GroupMappingUpdated", before, Mapping(mapping));
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken);

    private static UserAdministrationMapping Mapping(UserTeamGroupMapping mapping) =>
        new(mapping.Id, mapping.TeamId, mapping.GroupId, mapping.IsEnabled, mapping.Revision);

    private Task<UserAdministrationSnapshot> ExecuteAsync(ClaimsPrincipal administrator,
        Func<AlertDbContext, CancellationToken, Task<UserAdministrationSnapshot>> operation, CancellationToken token) =>
        ExecuteAsync(administrator, (context, _, cancellation) => operation(context, cancellation), token);

    private async Task<T> ExecuteAsync<T>(ClaimsPrincipal administrator,
        Func<AlertDbContext, Guid?, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        if (!directory.Enabled)
            throw new UnauthorizedAccessException("Identity administration requires enabled directory mode.");
        Guid? actor = authentication.IsOpen ? null : await directory.RequireAdministratorAsync(administrator, token);
        try
        {
            return await DatabaseTransactions.ExecuteAsync(factory, async context =>
            {
                if (actor.HasValue && (!await context.DirectoryUsers.AnyAsync(user => user.Id == actor && user.IsEnabled, token) ||
                    await context.ScimUserStates.AnyAsync(state => state.UserId == actor && (!state.IsActive || state.IsDeleted), token)))
                    throw new UnauthorizedAccessException("The administrator is disabled.");
                if (!authentication.IsOpen && !authentication.IsEntra && !await (from identity in context.SqlUserIdentities
                    join credential in EnabledSqlAdministrators(context) on identity.Username equals credential.Username
                    where identity.UserId == actor
                    select identity).AnyAsync(token))
                    throw new UnauthorizedAccessException("The SQL administrator role has changed.");
                return await operation(context, actor, token);
            }, token, IsolationLevel.Serializable);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(exception, "Concurrent identity administration change.");
            throw Error("Stale");
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or DbUpdateException)
        {
            logger.LogError(exception, "Identity administration database operation failed.");
            throw Error("Persistence");
        }
    }

    private static async Task<UserAdministrationSnapshot> ReadSnapshotAsync(AlertDbContext context, CancellationToken token)
    {
        var users = await context.DirectoryUsers.AsNoTracking().ToArrayAsync(token);
        var sqlRows = await (from credential in context.AuthenticationUsers.AsNoTracking()
            join identity in context.SqlUserIdentities on credential.Username equals identity.Username into links
            from identity in links.DefaultIfEmpty()
            select new { Credential = credential, UserId = (Guid?)identity.UserId }).ToArrayAsync(token);
        var entra = await context.EntraUserIdentities.AsNoTracking().ToDictionaryAsync(identity => identity.UserId, token);
        var states = await context.ScimUserStates.AsNoTracking().ToDictionaryAsync(state => state.UserId, token);
        var connections = await context.ScimConnections.AsNoTracking().ToDictionaryAsync(connection => connection.Id, token);
        var teams = await context.UserTeams.AsNoTracking().OrderBy(team => team.Name).ToArrayAsync(token);
        var inventoryRoles = await context.UserTeamInventoryRoles.AsNoTracking().ToArrayAsync(token);
        var observedRoles = await ObservedRolesAsync(context, token);
        var memberships = await context.UserTeamMemberships.AsNoTracking().ToArrayAsync(token);
        var mappings = await context.UserTeamGroupMappings.AsNoTracking().ToDictionaryAsync(mapping => mapping.Id, token);
        var groups = await context.ScimGroups.AsNoTracking().ToDictionaryAsync(group => group.Id, token);
        var removedMembers = (await context.ScimGroupMembers.AsNoTracking().Where(member => member.IsDeleted).ToArrayAsync(token))
            .Select(member => (member.GroupId, member.UserId)).ToHashSet();
        var credentialsByUser = sqlRows.Where(row => row.UserId.HasValue).ToDictionary(row => row.UserId!.Value, row => row.Credential);
        var teamsById = teams.ToDictionary(team => team.Id);
        var membershipsByUser = memberships.ToLookup(membership => membership.UserId);
        var result = new List<UserAdministrationUser>();
        var aliasLinks = await context.HistoricalUserAliases.AsNoTracking().ToArrayAsync(token);
        var historicalIds = (await HistoricalUserQueries.Profiles(context).Select(user => user.Id).ToArrayAsync(token)).ToHashSet();
        var reservedAliases = await HistoricalUserQueries.ReservedAliasesAsync(context, token);
        var profiledAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            var historical = historicalIds.Contains(user.Id);
            if (historical && reservedAliases.Contains(user.UserName)) continue;
            if (historical && !profiledAliases.Add(user.UserName)) throw Error("Binding");
            credentialsByUser.TryGetValue(user.Id, out var credential);
            entra.TryGetValue(user.Id, out var identity);
            states.TryGetValue(user.Id, out var state);
            var sources = membershipsByUser[user.Id].Select(membership =>
            {
                var team = teamsById[membership.TeamId];
                var mapping = membership.GroupMappingId is { } mappingId ? mappings[mappingId] : null;
                var group = mapping is not null ? groups[mapping.GroupId] : null;
                return new UserTeamAssignment(team.Id, team.Name, mapping is null,
                    team.IsEnabled && (mapping is null || mapping.IsEnabled && group!.IsActive && !group.IsDeleted &&
                        !removedMembers.Contains((group.Id, user.Id)) && state?.IsDeleted != true),
                    group?.DisplayName, group is not null ? connections[group.ConnectionId].Name : null);
            }).OrderBy(source => source.TeamName).ThenBy(source => source.GroupName).ToArray();
            var target = new Target(user, credential, identity, state, membershipsByUser[user.Id].ToArray(),
                historical ? user.UserName : null, aliasLinks.Where(link => link.AliasUserId == user.Id || link.MainUserId == user.Id).ToArray());
            CheckBinding(target);
            result.Add(View(target, state is not null ? connections[state.ConnectionId].Name : null, sources));
        }
        result.AddRange(sqlRows.Where(row => row.UserId is null)
            .Select(row => View(new(null, row.Credential, null, null, []), null, [])));
        foreach (var alias in await HistoricalUserQueries.AliasesAsync(context, token))
        {
            if (!reservedAliases.Contains(alias) && !profiledAliases.Contains(alias))
                result.Add(View(new(null, null, null, null, [], alias), null, []));
        }
        var historicalById = result.Where(user => user.IsHistorical && user.Id.HasValue).ToDictionary(user => user.Id!.Value);
        var usersById = users.ToDictionary(user => user.Id);
        result = result.Select(user =>
        {
            if (!user.IsHistorical || !user.Id.HasValue) return user;
            var parent = aliasLinks.SingleOrDefault(link => link.AliasUserId == user.Id.Value);
            return user with
            {
                HistoricalMainKey = parent is null ? null : "historical:" + usersById[parent.MainUserId].UserName,
                HistoricalMainAvailable = parent is not null && historicalById.ContainsKey(parent.MainUserId),
                HistoricalAliases = aliasLinks.Where(link => link.MainUserId == user.Id.Value)
                    .Select(link =>
                    {
                        var alias = usersById[link.AliasUserId];
                        return new UserAdministrationHistoricalAlias("historical:" + alias.UserName, alias.UserName,
                            UserReferenceQueries.Label(alias), historicalById.ContainsKey(alias.Id));
                    }).OrderBy(alias => alias.UserName, StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }).ToList();
        return new(result.OrderBy(user => user.EffectiveDisplayName).ThenBy(user => user.Key).ToArray(),
            teams.Select(team => new UserAdministrationTeam(team.Id, team.Name, team.IsEnabled, team.Revision)
            {
                InventoryRoleKeys = inventoryRoles.Where(role => role.TeamId == team.Id).Select(role => role.RoleKey)
                    .Order(StringComparer.Ordinal).ToArray()
            }).ToArray())
        {
            InventoryRoles = observedRoles.Keys.Union(inventoryRoles.Select(role => role.RoleKey), StringComparer.Ordinal)
                .Select(key => new UserAdministrationInventoryRole(key, observedRoles.GetValueOrDefault(key, key),
                    observedRoles.ContainsKey(key))).OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(role => role.Key, StringComparer.Ordinal).ToArray(),
            Groups = groups.Values.OrderBy(group => group.DisplayName).Select(group => new UserAdministrationGroup(
                group.Id, group.ConnectionId, connections[group.ConnectionId].Name, connections[group.ConnectionId].TenantId,
                group.ExternalId, group.DisplayName, group.IsActive, group.IsDeleted, group.SyncStatus, group.LastSyncedAt)).ToArray(),
            Mappings = mappings.Values.Select(Mapping).ToArray()
        };
    }

    private static UserAdministrationUser View(Target target, string? connection, IReadOnlyList<UserTeamAssignment> sources)
    {
        var user = target.User;
        var name = user?.UserName ?? target.HistoricalAlias ?? target.Credential!.Username;
        return new(target.Key, user?.Id, name,
            user?.DisplayName ?? name, user?.LocalDisplayName, user?.GivenName, user?.FamilyName,
            user?.Email, user?.Origin ?? (target.IsHistorical ? DirectoryUserOrigins.Legacy : DirectoryUserOrigins.Sql), target.Credential?.Username, target.Credential?.Role,
            target.Entra?.TenantId, target.Entra?.ObjectId, user?.IsEnabled ?? true, user?.UsesStableIdentity ?? false,
            target.State?.IsActive, target.State?.IsDeleted ?? false, target.State?.SyncStatus, target.State?.LastSyncedAt,
            connection, Version(target), sources) { IsHistorical = target.IsHistorical };
    }

    private static async Task<Target> ReadTargetAsync(AlertDbContext context, string key, CancellationToken token)
    {
        DirectoryUser? user = null;
        SqlAuthenticationUser? credential = null;
        string? historicalAlias = null;
        if (key.StartsWith("historical:", StringComparison.Ordinal))
        {
            var alias = key["historical:".Length..];
            if ((await HistoricalUserQueries.ReservedAliasesAsync(context, token)).Contains(alias))
                throw Error("HistoricalChanged");
            var profiles = await HistoricalUserQueries.Profiles(context).ToArrayAsync(token);
            var matches = profiles.Where(profile => profile.UserName.Equals(alias, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw Error("Binding");
            user = matches.SingleOrDefault();
            historicalAlias = user?.UserName ?? (await HistoricalUserQueries.AliasesAsync(context, token))
                .SingleOrDefault(value => value.Equals(alias, StringComparison.OrdinalIgnoreCase))
                ?? throw Error("HistoricalChanged");
            if (historicalAlias.Length > 256) throw Error("Profile");
        }
        else if (key.StartsWith("sql:", StringComparison.Ordinal))
        {
            var name = key[4..];
            credential = await context.AuthenticationUsers.SingleOrDefaultAsync(entry => entry.Username == name, token)
                ?? throw Error("Missing");
            user = await (from identity in context.SqlUserIdentities
                join entry in context.DirectoryUsers on identity.UserId equals entry.Id
                where identity.Username == credential.Username select entry).SingleOrDefaultAsync(token);
        }
        else if (key.StartsWith("user:", StringComparison.Ordinal) && Guid.TryParse(key[5..], out var id))
        {
            user = await context.DirectoryUsers.SingleOrDefaultAsync(entry => entry.Id == id, token) ?? throw Error("Missing");
            if (await HistoricalUserQueries.Profiles(context).AnyAsync(profile => profile.Id == id, token))
                throw Error("HistoricalChanged");
            credential = await (from identity in context.SqlUserIdentities
                join entry in context.AuthenticationUsers on identity.Username equals entry.Username
                where identity.UserId == id select entry).SingleOrDefaultAsync(token);
        }
        else throw Error("Missing");
        var entra = user is null ? null : await context.EntraUserIdentities.SingleOrDefaultAsync(identity => identity.UserId == user.Id, token);
        var state = user is null ? null : await context.ScimUserStates.SingleOrDefaultAsync(entry => entry.UserId == user.Id, token);
        var memberships = user is null ? [] : await context.UserTeamMemberships.Where(entry => entry.UserId == user.Id).ToArrayAsync(token);
        var aliasLinks = user is null ? [] : await context.HistoricalUserAliases
            .Where(link => link.AliasUserId == user.Id || link.MainUserId == user.Id).ToArrayAsync(token);
        var target = new Target(user, credential, entra, state, memberships, historicalAlias, aliasLinks);
        CheckBinding(target);
        return target;
    }

    private static void CheckBinding(Target target)
    {
        if (target.Credential is not null && (target.Entra is not null || target.State is not null))
            throw Error("Binding");
    }

    private static DirectoryUser BindSqlUser(AlertDbContext context, Target target)
    {
        if (!target.CanManageSql || target.User is not null) throw Error("Binding");
        var user = new DirectoryUser
        {
            UserName = target.Credential!.Username, DisplayName = target.Credential.Username, Origin = DirectoryUserOrigins.Sql
        };
        context.DirectoryUsers.Add(user);
        context.SqlUserIdentities.Add(new() { UserId = user.Id, Username = target.Credential.Username });
        return user;
    }

    private static DirectoryUser AddHistoricalProfile(AlertDbContext context, string alias)
    {
        var user = new DirectoryUser { UserName = alias, Origin = DirectoryUserOrigins.Legacy };
        context.DirectoryUsers.Add(user);
        return user;
    }

    private static void TouchProfile(DirectoryUser profile)
    {
        profile.Revision++;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task SetHistoricalAliasesAsync(AlertDbContext context, Target target, DirectoryUser main,
        IReadOnlyList<string> requested, CancellationToken token)
    {
        var keys = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Contains(target.Key) || keys.Any(key => string.IsNullOrWhiteSpace(key) ||
            !key.StartsWith("historical:", StringComparison.Ordinal))) throw Error("HistoricalAliases");
        var existing = target.AliasLinks.Where(link => link.MainUserId == main.Id).ToArray();
        var ids = existing.Select(link => link.AliasUserId).ToArray();
        var oldProfiles = await context.DirectoryUsers.Where(user => ids.Contains(user.Id)).ToDictionaryAsync(user => user.Id, token);
        var retained = existing.Where(link => keys.Contains("historical:" + oldProfiles[link.AliasUserId].UserName)).ToArray();
        foreach (var removed in existing.Except(retained))
        {
            context.HistoricalUserAliases.Remove(removed);
            TouchProfile(oldProfiles[removed.AliasUserId]);
        }
        var retainedKeys = retained.Select(link => "historical:" + oldProfiles[link.AliasUserId].UserName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys.Except(retainedKeys, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = await ReadTargetAsync(context, key, token);
            if (!candidate.IsHistorical || candidate.AliasLinks.Length > 0 || candidate.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase))
                throw Error("HistoricalAliases");
            var alias = candidate.User ?? AddHistoricalProfile(context, candidate.HistoricalAlias!);
            TouchProfile(alias);
            context.HistoricalUserAliases.Add(new() { AliasUserId = alias.Id, MainUserId = main.Id });
        }
    }

    private static void ApplyProfile(DirectoryUser user, SqlUserProfile profile)
    {
        user.DisplayName = OptionalText(profile.DisplayName, 256) ?? user.UserName;
        user.GivenName = OptionalText(profile.GivenName, 256);
        user.FamilyName = OptionalText(profile.FamilyName, 256);
        user.Email = OptionalText(profile.Email, 320);
    }

    private static string? OptionalText(string? value, int maximum)
    {
        var trimmed = value?.Trim();
        if (trimmed?.Length > maximum) throw Error("Profile");
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task SetManualTeamsAsync(AlertDbContext context, Guid userId, IReadOnlyList<Guid> requested, CancellationToken token)
    {
        var selected = requested.ToHashSet();
        var existing = await context.UserTeamMemberships
            .Where(membership => membership.UserId == userId && membership.GroupMappingId == null && membership.SourceGroupId == null)
            .ToArrayAsync(token);
        var oldIds = existing.Select(membership => membership.TeamId).ToHashSet();
        var added = selected.Except(oldIds).ToArray();
        if (selected.Contains(Guid.Empty) ||
            await context.UserTeams.CountAsync(team => added.Contains(team.Id) && team.IsEnabled, token) != added.Length)
            throw Error("Teams");
        context.UserTeamMemberships.RemoveRange(existing.Where(membership => !selected.Contains(membership.TeamId)));
        context.UserTeamMemberships.AddRange(added.Select(teamId => new UserTeamMembership { UserId = userId, TeamId = teamId }));
    }

    private static string RoleKey(string role) => UserTeamInventoryRole.NormalizeRoleKey(role);

    private static async Task<Dictionary<string, string>> ObservedRolesAsync(AlertDbContext context, CancellationToken token)
    {
        var roles = await context.ComputerInventory.AsNoTracking().Where(entry => entry.Role != null)
            .Select(entry => entry.Role!).ToArrayAsync(token);
        return roles.Where(role => !string.IsNullOrWhiteSpace(role)).Select(role => role.Trim())
            .Order(StringComparer.Ordinal).GroupBy(RoleKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static Task<string[]> TeamRoleKeysAsync(AlertDbContext context, Guid teamId, CancellationToken token) =>
        context.UserTeamInventoryRoles.Where(role => role.TeamId == teamId).OrderBy(role => role.RoleKey)
            .Select(role => role.RoleKey).ToArrayAsync(token);

    private static async Task SetInventoryRolesAsync(AlertDbContext context, Guid teamId,
        IReadOnlyList<string> requested, CancellationToken token)
    {
        if (requested.Any(role => string.IsNullOrWhiteSpace(role) || role.Trim().Length > 256))
            throw Error("InventoryRoles");
        var selected = requested.Select(RoleKey).ToHashSet(StringComparer.Ordinal);
        if (selected.Any(key => key.Length > 256)) throw Error("InventoryRoles");
        var existing = await context.UserTeamInventoryRoles.Where(role => role.TeamId == teamId).ToArrayAsync(token);
        var oldKeys = existing.Select(role => role.RoleKey).ToHashSet(StringComparer.Ordinal);
        var observed = await ObservedRolesAsync(context, token);
        var added = selected.Except(oldKeys, StringComparer.Ordinal).ToArray();
        if (added.Any(key => !observed.ContainsKey(key))) throw Error("InventoryRoles");
        context.UserTeamInventoryRoles.RemoveRange(existing.Where(role => !selected.Contains(role.RoleKey)));
        context.UserTeamInventoryRoles.AddRange(added.Select(key => new UserTeamInventoryRole { TeamId = teamId, RoleKey = key }));
    }

    private static async Task RequireOtherSqlAdminAsync(AlertDbContext context, Target target, CancellationToken token)
    {
        if (target.Credential?.Role != SqlAuthenticationRoles.Admin || target.User?.IsEnabled == false) return;
        var name = target.Credential.Username;
        var other = await EnabledSqlAdministrators(context).AnyAsync(credential => credential.Username != name, token);
        if (!other) throw Error("LastEffectiveAdmin");
    }

    private static IQueryable<SqlAuthenticationUser> EnabledSqlAdministrators(AlertDbContext context) =>
        context.AuthenticationUsers.Where(credential => credential.Role == SqlAuthenticationRoles.Admin &&
            !context.SqlUserIdentities.Any(identity => identity.Username == credential.Username &&
                (!context.DirectoryUsers.Any(user => user.Id == identity.UserId && user.IsEnabled) ||
                 context.EntraUserIdentities.Any(entra => entra.UserId == identity.UserId) ||
                 context.ScimUserStates.Any(state => state.UserId == identity.UserId && (!state.IsActive || state.IsDeleted)))) &&
            (!context.LegacyUserLinks.Any(link => link.Status == LegacyUserLinkStatuses.Confirmed && link.Alias == credential.Username) ||
             context.SqlUserIdentities.Any(identity => identity.Username == credential.Username &&
                 context.DirectoryUsers.Any(user => user.Id == identity.UserId && user.UsesStableIdentity))));

    private static string Version(Target target) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        target.User, target.Entra, target.State, target.Credential, target.HistoricalAlias,
        HistoricalAliases = target.AliasLinks.OrderBy(link => link.AliasUserId),
        Memberships = target.Memberships.OrderBy(membership => membership.Id)
    })));

    private static void RequireVersion(Target target, string version)
    {
        if (!string.Equals(Version(target), version, StringComparison.Ordinal)) throw Error("Stale");
    }

    private static object AuditUser(Target target) => JsonSerializer.SerializeToElement(new
    {
        User = target.User,
        target.HistoricalAlias,
        HistoricalAliases = target.AliasLinks.OrderBy(link => link.AliasUserId),
        SqlUsername = target.Credential?.Username,
        SqlRole = target.Credential?.Role,
        ManualTeams = target.Memberships.Where(membership => membership.GroupMappingId is null)
            .Select(membership => membership.TeamId).Order()
    });

    private static void Audit(AlertDbContext context, Guid? actor, Guid? user, Guid? team, string action, object? before, object? after) =>
        context.IdentityAdministrationRecords.Add(new()
        {
            ActorUserId = actor, IsOpenAuthentication = !actor.HasValue, TargetUserId = user, TargetTeamId = team, Action = action,
            BeforeJson = JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after)
        });

    private static UserAdministrationException Error(string name) => new("UserDirectory.Errors." + name);
    private sealed record Target(DirectoryUser? User, SqlAuthenticationUser? Credential, EntraUserIdentity? Entra,
        ScimUserState? State, UserTeamMembership[] Memberships, string? HistoricalAlias = null,
        HistoricalUserAlias[]? HistoricalLinks = null)
    {
        public HistoricalUserAlias[] AliasLinks => HistoricalLinks ?? [];
        public bool IsHistorical => HistoricalAlias is not null;
        public string Key => IsHistorical ? "historical:" + HistoricalAlias :
            Credential is not null ? "sql:" + Credential.Username : DirectoryIdentity.OwnerKey(User!.Id);
        public bool CanManageSql => Credential is not null && Entra is null && State is null;
    }
}
