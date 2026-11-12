using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MonitoringApp.Services.EFCore.Migrations;

namespace MonitoringApp.Tests;

public sealed class IdentityDirectoryTests
{
    internal const string MigrationId = "20261110112945_AddIdentityDirectory";
    internal static readonly Type[] EntityTypes =
    [
        typeof(DirectoryUser), typeof(SqlUserIdentity), typeof(EntraUserIdentity),
        typeof(ScimConnection), typeof(ScimUserState), typeof(ScimGroup), typeof(ScimGroupMember),
        typeof(UserTeam), typeof(UserTeamGroupMapping), typeof(UserTeamMembership), typeof(LegacyUserLink)
    ];

    [Fact]
    public async Task DirectoryAndManualTeamsWorkWithoutAnyProvisioningConnection()
    {
        await using var database = await Database.CreateAsync(seed: false);
        var user = new DirectoryUser { UserName = "local", Origin = DirectoryUserOrigins.Sql };
        var first = new UserTeam { Name = "First" };
        var second = new UserTeam { Name = "Second" };
        await using (var context = database.CreateDbContext())
        {
            context.AddRange(user, first, second,
                new SqlAuthenticationUser { Username = "local", PasswordHash = "keep-hash", Role = "Admin" },
                new SqlUserIdentity { UserId = user.Id, Username = "local" },
                new UserTeamMembership { UserId = user.Id, TeamId = first.Id },
                new UserTeamMembership { UserId = user.Id, TeamId = second.Id });
            await context.SaveChangesAsync();
        }
        await using var verify = database.CreateDbContext();
        Assert.Equal(2, await verify.UserTeamMemberships.Select(entry => entry.TeamId).Distinct().CountAsync());
        Assert.Empty(await verify.ScimConnections.ToArrayAsync());
        Assert.Empty(await verify.ScimUserStates.ToArrayAsync());
        Assert.Equal("keep-hash", (await verify.AuthenticationUsers.SingleAsync()).PasswordHash);
        Assert.Equal(user.Id, (await verify.SqlUserIdentities.SingleAsync()).UserId);
    }

    [Fact]
    public async Task MutableProfileAndLocalDisplayNameAreSeparateFromStableIdentityAndStatus()
    {
        await using var database = await Database.CreateAsync();
        var id = database.Data!.EntraUser.Id;
        var tenant = database.Data.Connection.TenantId;
        var objectId = database.Data.State.ExternalId;
        await using (var context = database.CreateDbContext())
        {
            var user = await context.DirectoryUsers.FindAsync(id);
            Assert.NotNull(user);
            user.UserName = "renamed@contoso.test";
            user.DisplayName = "Updated source display";
            user.GivenName = "Updated";
            user.FamilyName = "Source";
            user.Email = "updated@contoso.test";
            user.Revision++;
            var state = await context.ScimUserStates.FindAsync(id);
            Assert.NotNull(state);
            state.IsActive = false;
            state.Revision++;
            await context.SaveChangesAsync();
        }
        await using var verify = database.CreateDbContext();
        var stored = await verify.DirectoryUsers.SingleAsync(user => user.Id == id);
        Assert.Equal("Local display", stored.LocalDisplayName);
        Assert.Equal("Updated source display", stored.DisplayName);
        Assert.Equal("Updated", stored.GivenName);
        Assert.Equal("Source", stored.FamilyName);
        Assert.Equal("updated@contoso.test", stored.Email);
        Assert.True(stored.IsEnabled);
        Assert.False((await verify.ScimUserStates.SingleAsync()).IsActive);
        var identity = await verify.EntraUserIdentities.SingleAsync();
        Assert.Equal((id, tenant, objectId), (identity.UserId, identity.TenantId, identity.ObjectId));
        Assert.False((await verify.ScimConnections.SingleAsync()).IsEnabled);
        Assert.Equal(2, await verify.UserTeamMemberships.CountAsync());
    }

    [Fact]
    public async Task EntraPairIsUniqueButEmailAndObjectIdInAnotherTenantDoNotMergeUsers()
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        var duplicate = new DirectoryUser { Email = data.EntraUser.Email };
        await database.RejectAsync(duplicate, new EntraUserIdentity
        {
            UserId = duplicate.Id, TenantId = data.Connection.TenantId, ObjectId = data.State.ExternalId
        });
        var otherTenant = new DirectoryUser { Email = data.EntraUser.Email };
        await using (var context = database.CreateDbContext())
        {
            context.AddRange(otherTenant, new EntraUserIdentity
            {
                UserId = otherTenant.Id, TenantId = Guid.NewGuid(), ObjectId = data.State.ExternalId
            });
            await context.SaveChangesAsync();
        }
        await using var verify = database.CreateDbContext();
        Assert.Equal(2, await verify.EntraUserIdentities.CountAsync());
        Assert.Equal(2, await verify.DirectoryUsers.CountAsync(user => user.Email == data.EntraUser.Email));
    }

    [Fact]
    public async Task SqlCredentialCanOnlyBeLinkedOnceAndMustExist()
    {
        await using var database = await Database.CreateAsync();
        var other = new DirectoryUser();
        await database.RejectAsync(other, new SqlUserIdentity { UserId = other.Id, Username = "admin" });
        await database.RejectAsync(other, new SqlUserIdentity { UserId = other.Id, Username = "missing" });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProvisioningCannotDisagreeWithEntraTenantOrObject(bool wrongTenant)
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        await using (var context = database.CreateDbContext())
        {
            context.ScimUserStates.Remove(await context.ScimUserStates.SingleAsync());
            context.ScimGroupMembers.RemoveRange(await context.ScimGroupMembers.ToArrayAsync());
            context.UserTeamMemberships.RemoveRange(await context.UserTeamMemberships.Where(row => row.GroupMappingId != null).ToArrayAsync());
            await context.SaveChangesAsync();
        }
        await database.RejectAsync(new ScimUserState
        {
            UserId = data.EntraUser.Id, ConnectionId = data.Connection.Id,
            TenantId = wrongTenant ? Guid.NewGuid() : data.Connection.TenantId,
            ExternalId = wrongTenant ? data.State.ExternalId : Guid.NewGuid()
        });
    }

    [Fact]
    public async Task GroupMembershipCannotCrossProvisioningConnections()
    {
        await using var database = await Database.CreateAsync();
        var otherConnection = new ScimConnection { Name = "Other", TenantId = Guid.NewGuid() };
        var otherGroup = new ScimGroup { ConnectionId = otherConnection.Id, ExternalId = Guid.NewGuid() };
        await database.RejectAsync(otherConnection, otherGroup, new ScimGroupMember
        {
            GroupId = otherGroup.Id, UserId = database.Data!.EntraUser.Id, ConnectionId = otherConnection.Id
        });
    }

    [Fact]
    public async Task ManualAndMultipleGroupSourcesCoexistAndRemovingOnePreservesTheOthers()
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        var group = new ScimGroup { ConnectionId = data.Connection.Id, ExternalId = Guid.NewGuid() };
        var mapping = new UserTeamGroupMapping { TeamId = data.Team.Id, GroupId = group.Id };
        await using (var context = database.CreateDbContext())
        {
            context.AddRange(group, mapping,
                new ScimGroupMember { GroupId = group.Id, UserId = data.EntraUser.Id, ConnectionId = data.Connection.Id },
                new UserTeamMembership
                {
                    UserId = data.EntraUser.Id, TeamId = data.Team.Id,
                    GroupMappingId = mapping.Id, SourceGroupId = group.Id
                });
            await context.SaveChangesAsync();
            Assert.Equal(3, await context.UserTeamMemberships.CountAsync());
            Assert.Single(await context.UserTeamMemberships.Select(row => row.TeamId).Distinct().ToArrayAsync());
            context.UserTeamMemberships.Remove(await context.UserTeamMemberships.SingleAsync(row => row.GroupMappingId == mapping.Id));
            await context.SaveChangesAsync();
            context.ScimGroupMembers.Remove(await context.ScimGroupMembers.SingleAsync(row => row.GroupId == group.Id));
            await context.SaveChangesAsync();
        }
        await using var verify = database.CreateDbContext();
        Assert.Equal(2, await verify.UserTeamMemberships.CountAsync());
        Assert.Single(await verify.UserTeamMemberships.Where(row => row.GroupMappingId == null).ToArrayAsync());
        Assert.Single(await verify.UserTeamMemberships.Where(row => row.GroupMappingId == data.Mapping.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InvalidOrDuplicateMembershipSourcesAreRejected(int scenario)
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        var row = new UserTeamMembership { UserId = data.EntraUser.Id, TeamId = data.Team.Id };
        if (scenario == 1)
            (row.GroupMappingId, row.SourceGroupId) = (data.Mapping.Id, data.Group.Id);
        if (scenario == 2)
            row.GroupMappingId = data.Mapping.Id;
        if (scenario == 3)
            row.SourceGroupId = data.Group.Id;
        if (scenario == 4)
        {
            row.UserId = data.SqlUser.Id;
            (row.GroupMappingId, row.SourceGroupId) = (data.Mapping.Id, data.Group.Id);
        }
        await database.RejectAsync(row);
    }

    [Fact]
    public async Task MappingCannotGrantMembershipInADifferentTeam()
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        var other = new UserTeam { Name = "Other team" };
        await database.RejectAsync(other, new UserTeamMembership
        {
            UserId = data.EntraUser.Id, TeamId = other.Id,
            GroupMappingId = data.Mapping.Id, SourceGroupId = data.Group.Id
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ConfirmationRequiresTargetApproverTimestampAndEvidence(int missing)
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        var link = new LegacyUserLink
        {
            Alias = "verified@contoso.test", Status = LegacyUserLinkStatuses.Confirmed,
            TargetUserId = data.EntraUser.Id, ApprovedByUserId = data.SqlUser.Id,
            ApprovedAt = DateTimeOffset.UtcNow, VerificationNote = "Verified test evidence"
        };
        if (missing == 0) link.TargetUserId = null;
        if (missing == 1) link.ApprovedByUserId = null;
        if (missing == 2) link.ApprovedAt = null;
        if (missing == 3) link.VerificationNote = " ";
        await database.RejectAsync(link);
    }

    [Fact]
    public async Task ConfirmedAliasesRetainEvidenceAndAreUniqueWithinTenantContext()
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        var link = new LegacyUserLink
        {
            Alias = "verified@contoso.test", Status = LegacyUserLinkStatuses.Confirmed,
            TargetUserId = data.EntraUser.Id, ApprovedByUserId = data.SqlUser.Id,
            ApprovedAt = DateTimeOffset.UtcNow, VerificationNote = "Verified test evidence"
        };
        await using (var context = database.CreateDbContext())
        {
            context.Add(link);
            await context.SaveChangesAsync();
        }
        await database.RejectAsync(new LegacyUserLink { Alias = link.Alias });
        await using var verify = database.CreateDbContext();
        verify.Add(new LegacyUserLink { Alias = link.Alias, TenantId = Guid.NewGuid() });
        await verify.SaveChangesAsync();
        var stored = await verify.LegacyUserLinks.SingleAsync(row => row.Id == link.Id);
        Assert.Equal(data.EntraUser.Id, stored.TargetUserId);
        Assert.Equal("Verified test evidence", stored.VerificationNote);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task RevisionsRejectStaleDirectoryAndConfigurationWrites(int kind)
    {
        await using var database = await Database.CreateAsync();
        var data = database.Data!;
        var (type, key) = kind switch
        {
            0 => (typeof(DirectoryUser), data.EntraUser.Id),
            1 => (typeof(UserTeam), data.Team.Id),
            2 => (typeof(ScimConnection), data.Connection.Id),
            3 => (typeof(ScimUserState), data.State.UserId),
            4 => (typeof(ScimGroup), data.Group.Id),
            5 => (typeof(UserTeamGroupMapping), data.Mapping.Id),
            _ => (typeof(LegacyUserLink), data.LegacyLink.Id)
        };
        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var firstEntry = await first.FindAsync(type, key);
        var secondEntry = await second.FindAsync(type, key);
        Assert.NotNull(firstEntry);
        Assert.NotNull(secondEntry);
        first.Entry(firstEntry).Property("Revision").CurrentValue = 2;
        await first.SaveChangesAsync();
        second.Entry(secondEntry).Property("Revision").CurrentValue = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task IdentityReferencesNeverCascadeDeleteAndReferencedUsersCannotDisappear()
    {
        await using var database = await Database.CreateAsync();
        await using var context = database.CreateDbContext();
        foreach (var type in EntityTypes)
            Assert.All(context.Model.FindEntityType(type)!.GetForeignKeys(),
                foreignKey => Assert.Equal(DeleteBehavior.NoAction, foreignKey.DeleteBehavior));
        context.DirectoryUsers.Remove((await context.DirectoryUsers.FindAsync(database.Data!.EntraUser.Id))!);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public void MigrationOnlyCreatesDirectorySchemaWithoutChangingExistingRowsOrCredentials()
    {
        var migration = new AddIdentityDirectory();
        Assert.Equal(11, migration.UpOperations.OfType<CreateTableOperation>().Count());
        Assert.All(migration.UpOperations, operation =>
            Assert.True(operation is CreateTableOperation or CreateIndexOperation, operation.GetType().Name));
        Assert.All(migration.DownOperations, operation => Assert.IsType<DropTableOperation>(operation));
        Assert.Equal(11, migration.DownOperations.Count);
        using var context = SqlServerModel();
        var expected = EntityTypes.Select(type => context.Model.FindEntityType(type)!.GetTableName()).Order().ToArray();
        Assert.Equal(expected, migration.UpOperations.OfType<CreateTableOperation>().Select(operation => operation.Name).Order());
        var credentials = context.Model.FindEntityType(typeof(SqlAuthenticationUser))!;
        Assert.Equal(["PasswordHash", "Role", "Username"], credentials.GetProperties().Select(property => property.Name).Order());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void UpgradeScriptAndFreshInstallTemplateContainTheExactGeneratedMigration()
    {
        using var context = SqlServerModel();
        var generated = context.GetService<IMigrator>().GenerateScript(
            "20261109082252_AddInventoryLastBootTime", MigrationId, MigrationsSqlGenerationOptions.Idempotent);
        static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var packaged = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "UpgradeIdentityDirectory.sql"));
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        Assert.Equal(Normalize(generated), Normalize(packaged));
        Assert.Contains(Normalize(generated), Normalize(template));
        Assert.DoesNotContain("DELETE FROM", generated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE [", generated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DomainContractsHaveNoDatabaseDependencies()
    {
        Assert.Empty(PersistenceArchitecture.FindViolations(typeof(DirectoryUser).Assembly, _ => false, EntityTypes));
    }

    private static AlertDbContext SqlServerModel() => new(new DbContextOptionsBuilder<AlertDbContext>()
        .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=IdentityModelOnly;Integrated Security=True;TrustServerCertificate=True").Options);

    private sealed class Database(SqliteConnection connection, DbContextOptions<AlertDbContext> options)
        : IDbContextFactory<AlertDbContext>, IAsyncDisposable
    {
        public IdentityDirectoryTestData? Data { get; private set; }
        public AlertDbContext CreateDbContext() => new(options);

        public async Task RejectAsync(params object[] entries)
        {
            await using var context = CreateDbContext();
            context.AddRange(entries);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        public static async Task<Database> CreateAsync(bool seed = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var database = new Database(connection, new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = database.CreateDbContext();
            var tables = EntityTypes.Append(typeof(SqlAuthenticationUser)).Select(type => context.Model.FindEntityType(type)!.GetTableName()).ToArray();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => tables.Any(name =>
                    statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal) ||
                    statement.Contains($" ON \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement);
            if (seed)
            {
                context.AuthenticationUsers.Add(new() { Username = "admin", PasswordHash = "keep-hash", Role = "Admin" });
                database.Data = IdentityDirectoryTestData.AddTo(context, "admin");
                await context.SaveChangesAsync();
            }
            return database;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

internal sealed record IdentityDirectoryTestData(
    DirectoryUser SqlUser, DirectoryUser EntraUser, ScimConnection Connection, ScimUserState State,
    ScimGroup Group, UserTeam Team, UserTeamGroupMapping Mapping, LegacyUserLink LegacyLink)
{
    public static IdentityDirectoryTestData AddTo(AlertDbContext context, string sqlUsername)
    {
        var sql = new DirectoryUser { UserName = sqlUsername, Origin = DirectoryUserOrigins.Sql };
        var entra = new DirectoryUser
        {
            UserName = "operator@contoso.test", DisplayName = "Source display", LocalDisplayName = "Local display",
            GivenName = "Source", FamilyName = "Profile", Email = "operator@contoso.test", Origin = DirectoryUserOrigins.EntraLogin
        };
        var connection = new ScimConnection { Name = "Test connection", TenantId = Guid.NewGuid() };
        var state = new ScimUserState
        {
            UserId = entra.Id, ConnectionId = connection.Id, TenantId = connection.TenantId, ExternalId = Guid.NewGuid()
        };
        var group = new ScimGroup { ConnectionId = connection.Id, ExternalId = Guid.NewGuid(), DisplayName = "Source group" };
        var team = new UserTeam { Name = "Operations" };
        var mapping = new UserTeamGroupMapping { TeamId = team.Id, GroupId = group.Id };
        var legacy = new LegacyUserLink { Alias = "old@contoso.test" };
        context.AddRange(sql, entra, connection, state, group, team, mapping, legacy,
            new SqlUserIdentity { UserId = sql.Id, Username = sqlUsername },
            new EntraUserIdentity { UserId = entra.Id, TenantId = connection.TenantId, ObjectId = state.ExternalId },
            new ScimGroupMember { GroupId = group.Id, UserId = entra.Id, ConnectionId = connection.Id },
            new UserTeamMembership { UserId = entra.Id, TeamId = team.Id },
            new UserTeamMembership { UserId = entra.Id, TeamId = team.Id, GroupMappingId = mapping.Id, SourceGroupId = group.Id });
        return new(sql, entra, connection, state, group, team, mapping, legacy);
    }
}
