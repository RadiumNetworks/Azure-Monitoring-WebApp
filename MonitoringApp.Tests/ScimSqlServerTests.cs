using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class ScimSqlServerTests
{
    [LocalScimSqlFact]
    public async Task HistoricalAliasUpgradeIsAdditiveIdempotentAndConcurrentGroupingAndResetPreserveProfiles()
    {
        var database = "MonitoringApp_Historical_Aliases_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var scripts = Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts");
        var upgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeHistoricalUserAliases.sql")).Trim();
        var template = File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql"));
        var section = template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..].Replace("<database-name>", database);
        Assert.Contains(upgrade, section);
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            await ScriptAsync(connection.ConnectionString, section.Replace(upgrade, ""));
            await ExecuteAsync(connection.ConnectionString, """
                IF OBJECT_ID('dbo.HistoricalUserAliases') IS NOT NULL
                    THROW 51031, 'Expected historical profiles without display alias schema.', 1;
                """);
            var options = new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer(connection.ConnectionString, sql => sql.EnableRetryOnFailure()).Options;
            var first = new DirectoryUser { UserName = "first-main@example.test", LocalDisplayName = "First main" };
            var second = new DirectoryUser { UserName = "second-main@example.test", LocalDisplayName = "Second main" };
            var alias = new DirectoryUser
            {
                UserName = "admin-alias@example.test", DisplayName = "Original alias", LocalDisplayName = "Original local alias",
                GivenName = "Original first", FamilyName = "Original last", Email = "contact-only@example.test"
            };
            await using (var context = new AlertDbContext(options))
            {
                context.DirectoryUsers.AddRange(first, second, alias);
                context.AuthenticationUsers.Add(new() { Username = "preserved-reader", Role = "Reader", PasswordHash = "unchanged-hash" });
                context.RootCauseAnalyses.Add(new()
                {
                    Id = Guid.NewGuid(), AssignedTo = alias.UserName, Revision = 1, CreatedAt = DateTimeOffset.UtcNow,
                    FollowUpDueAt = DateTimeOffset.UtcNow.AddHours(1)
                });
                await context.SaveChangesAsync();
            }
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ExecuteAsync(connection.ConnectionString, """
                IF EXISTS (SELECT 1 FROM dbo.HistoricalUserAliases)
                    THROW 51032, 'The upgrade must not infer alias assignments.', 1;
                IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = '20261111131740_AddHistoricalUserAliases') <> 1
                    THROW 51033, 'Expected exactly one historical alias migration entry.', 1;
                """);
            await using var host = await ScimTestHost.CreateAsync(authentication: "open", databaseOptions: options);
            var auth = new ApplicationAuthenticationOptions { Type = "open" };
            var directory = new IdentityDirectoryOptions { Enabled = true };
            var administration = new UserAdministrationService(host, new(host, directory, auth), auth,
                new(host, new(), directory), Microsoft.Extensions.Logging.Abstractions.NullLogger<UserAdministrationService>.Instance);
            var caller = new ClaimsPrincipal(new ClaimsIdentity());
            var snapshot = await administration.GetSnapshotAsync(caller);
            async Task<string> GroupAsync(Guid mainId)
            {
                var main = snapshot.Users.Single(user => user.Id == mainId);
                try
                {
                    await administration.SaveUserAsync(caller, new(main.Key, main.Version, main.LocalDisplayName, true, [],
                        HistoricalAliasKeys: ["historical:" + alias.UserName]));
                    return "Saved";
                }
                catch (UserAdministrationException exception) { return exception.ResourceKey; }
            }
            var results = await Task.WhenAll(GroupAsync(first.Id), GroupAsync(second.Id));
            Assert.Single(results, result => result == "Saved");
            Assert.Single(results, result => result == "UserDirectory.Errors.HistoricalAliases");
            await using (var check = host.CreateDbContext())
            {
                var link = Assert.Single(await check.HistoricalUserAliases.ToArrayAsync());
                Assert.Equal(alias.Id, link.AliasUserId);
                Assert.Contains(link.MainUserId, new[] { first.Id, second.Id });
                Assert.Equal(alias.UserName, (await check.RootCauseAnalyses.SingleAsync()).AssignedTo);
                Assert.Equal(3, await check.DirectoryUsers.CountAsync());
                Assert.Empty(await check.EntraUserIdentities.ToArrayAsync());
                Assert.Empty(await check.SqlUserIdentities.ToArrayAsync());
                Assert.Empty(await check.LegacyUserLinks.ToArrayAsync());
            }
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection.ConnectionString,
                $"INSERT INTO dbo.HistoricalUserAliases (AliasUserId, MainUserId) VALUES ('{first.Id}', '{first.Id}');"))).Number);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection.ConnectionString,
                $"INSERT INTO dbo.HistoricalUserAliases (AliasUserId, MainUserId) VALUES ('{Guid.NewGuid()}', '{first.Id}');"))).Number);
            Assert.Equal(2627, (await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection.ConnectionString,
                $"INSERT INTO dbo.HistoricalUserAliases (AliasUserId, MainUserId) VALUES ('{alias.Id}', '{first.Id}');"))).Number);
            var reset = File.ReadAllText(Path.Combine(scripts, "ResetMonitoringData.sql"))
                .Replace("DECLARE @ExpectedDatabase sysname = N'<database-name>';", $"DECLARE @ExpectedDatabase sysname = N'{database}';")
                .Replace("DECLARE @Confirmation nvarchar(32) = N'';", "DECLARE @Confirmation nvarchar(32) = N'RESET';")
                .Replace("DECLARE @CommitChanges bit = 0;", "DECLARE @CommitChanges bit = 1;");
            await ScriptAsync(connection.ConnectionString, reset);
            await using (var check = host.CreateDbContext())
            {
                Assert.Single(await check.HistoricalUserAliases.ToArrayAsync());
                Assert.Equal(3, await check.DirectoryUsers.CountAsync());
                Assert.Equal("unchanged-hash", (await check.AuthenticationUsers.SingleAsync()).PasswordHash);
                Assert.Empty(await check.RootCauseAnalyses.ToArrayAsync());
                var original = await check.DirectoryUsers.SingleAsync(user => user.Id == alias.Id);
                Assert.Equal("Original alias", original.DisplayName);
                Assert.Equal("Original local alias", original.LocalDisplayName);
                Assert.Equal("Original first", original.GivenName);
                Assert.Equal("Original last", original.FamilyName);
                Assert.Equal("contact-only@example.test", original.Email);
            }
            var separated = (await administration.GetSnapshotAsync(caller)).Users.Single(user => user.Id == alias.Id);
            await administration.DetachHistoricalAliasAsync(caller, separated.Key, separated.Version);
            await using var final = host.CreateDbContext();
            Assert.Empty(await final.HistoricalUserAliases.ToArrayAsync());
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    [LocalScimSqlFact]
    public async Task IsolatedReplicationWarningRetirementPreservesHistoricalAlertsAndOtherRulesAndFreshInstallIsIdempotent()
    {
        var database = "MonitoringApp_KQL_Retirement_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var scripts = Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts");
        var template = File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql"));
        var upgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeRetireReplicationInventoryWarning.sql")).Trim();
        var section = template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..].Replace("<database-name>", database);
        Assert.Contains(upgrade, section);
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            await ScriptAsync(connection.ConnectionString, section.Replace(upgrade, ""));
            var options = new DbContextOptionsBuilder<AlertDbContext>().UseSqlServer(connection.ConnectionString).Options;
            var ruleId = Guid.NewGuid();
            var scheduled = ScheduledQueryRule.Example(false);
            scheduled.Enabled = true;
            scheduled.WorkspaceKey = "preserved-workspace";
            var source = new AlertRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "preserved-source", "Replication", "Sev1",
                "New", "Log", "Fired", "KNOWN", "", "sub", DateTimeOffset.UtcNow, "Preserved", "", "", "{}");
            AlertRecord historical;
            await using (var context = new AlertDbContext(options))
            {
                var legacy = await context.AlertRules.SingleAsync(rule => rule.RuleType == AlertRuleTypes.ReplicationInventoryWarning);
                Assert.True(legacy.Enabled);
                historical = ReplicationInventoryWarning.Create(source, legacy, "RETIRED");
                context.Alerts.AddRange(source, historical);
                context.AlertRules.Add(new() { Id = ruleId, Name = "Unrelated preserved rule", Enabled = true });
                context.ScheduledQueryRules.Add(scheduled);
                await context.SaveChangesAsync();
            }
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await using (var context = new AlertDbContext(options))
            {
                Assert.False((await context.AlertRules.SingleAsync(rule => rule.RuleType == AlertRuleTypes.ReplicationInventoryWarning)).Enabled);
                Assert.True((await context.AlertRules.SingleAsync(rule => rule.Id == ruleId)).Enabled);
                Assert.Equal(2, await context.Alerts.CountAsync());
                Assert.Equal(historical.RawJson, (await context.Alerts.SingleAsync(alert => alert.Id == historical.Id)).RawJson);
                Assert.Equal(source.RawJson, (await context.Alerts.SingleAsync(alert => alert.Id == source.Id)).RawJson);
                var preserved = await context.ScheduledQueryRules.SingleAsync();
                Assert.True(preserved.Enabled);
                Assert.Equal(scheduled.Kql, preserved.Kql);
                Assert.Equal(scheduled.WorkspaceKey, preserved.WorkspaceKey);
            }
            await ExecuteAsync(connection.ConnectionString, """
                IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'20261111120000_RetireReplicationInventoryWarning')<>1
                    THROW 51041, 'Retirement migration was not recorded exactly once.', 1;
                """);
            using (var pool = new SqlConnection(connection.ConnectionString)) SqlConnection.ClearPool(pool);
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; CREATE DATABASE [{database}];");
            await ScriptAsync(connection.ConnectionString, section);
            await ScriptAsync(connection.ConnectionString, section);
            await using (var context = new AlertDbContext(options))
            {
                Assert.False((await context.AlertRules.SingleAsync(rule => rule.RuleType == AlertRuleTypes.ReplicationInventoryWarning)).Enabled);
                Assert.Empty(await context.Alerts.ToArrayAsync());
                Assert.Empty(await context.ScheduledQueryRules.ToArrayAsync());
            }
            var reset = File.ReadAllText(Path.Combine(scripts, "ResetMonitoringData.sql"))
                .Replace("DECLARE @ExpectedDatabase sysname = N'<database-name>';", $"DECLARE @ExpectedDatabase sysname = N'{database}';")
                .Replace("DECLARE @Confirmation nvarchar(32) = N'';", "DECLARE @Confirmation nvarchar(32) = N'RESET';")
                .Replace("DECLARE @CommitChanges bit = 0;", "DECLARE @CommitChanges bit = 1;");
            await ScriptAsync(connection.ConnectionString, reset);
            await ScriptAsync(connection.ConnectionString, section);
            await ScriptAsync(connection.ConnectionString, section);
            await using (var context = new AlertDbContext(options))
            {
                var rules = (await context.AlertRules.AsNoTracking().ToArrayAsync()).OrderBy(rule => rule.Id).ToArray();
                Assert.Equal(10, rules.Length);
                Assert.DoesNotContain(rules, rule => rule.RuleType == AlertRuleTypes.ReplicationInventoryWarning);
                Assert.Equal(JsonSerializer.Serialize(DefaultAlertRules.Create().OrderBy(rule => rule.Id)),
                    JsonSerializer.Serialize(rules));
            }
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString,
                    $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    [LocalScimSqlFact]
    public async Task IsolatedAp3UpgradeAndFreshInstallationAreIdempotentWithoutChangingExistingUsers()
    {
        var database = "MonitoringApp_SCIM_AP4_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var scripts = Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts");
        var template = File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql"));
        var upgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeScimUserLifecycle.sql")).Trim();
        var section = template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..].Replace("<database-name>", database);
        Assert.Contains(upgrade, section);
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            var administrationUpgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeIdentityAdministration.sql")).Trim();
            var groupUpgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeScimGroupLifecycle.sql")).Trim();
            var openRolesUpgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeOpenAdministrationAndTeamInventoryRoles.sql")).Trim();
            await ScriptAsync(connection.ConnectionString, section.Replace(upgrade, "").Replace(administrationUpgrade, "")
                .Replace(groupUpgrade, "").Replace(openRolesUpgrade, ""));
            await ExecuteAsync(connection.ConnectionString, """
                IF COL_LENGTH('dbo.ScimUserStates', 'IsDeleted') IS NOT NULL
                    THROW 51000, 'Expected the AP3 baseline.', 1;
                INSERT dbo.ScimConnections (Id, Name, TenantId, IsEnabled, Revision)
                VALUES ('01000000-0000-0000-0000-000000000001', N'Synthetic source', '02000000-0000-0000-0000-000000000001', 1, 1);
                INSERT dbo.DirectoryUsers (Id, UserName, DisplayName, Origin, IsEnabled, UsesStableIdentity, Revision, CreatedAt, UpdatedAt)
                VALUES ('03000000-0000-0000-0000-000000000001', N'original@contoso.test', N'Original', N'Scim', 1, 1, 7,
                    SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
                INSERT dbo.EntraUserIdentities (UserId, TenantId, ObjectId)
                VALUES ('03000000-0000-0000-0000-000000000001', '02000000-0000-0000-0000-000000000001', '04000000-0000-0000-0000-000000000001');
                INSERT dbo.ScimUserStates (UserId, ConnectionId, TenantId, ExternalId, IsActive, SyncStatus, Revision)
                VALUES ('03000000-0000-0000-0000-000000000001', '01000000-0000-0000-0000-000000000001',
                    '02000000-0000-0000-0000-000000000001', '04000000-0000-0000-0000-000000000001', 0, N'Succeeded', 3);
                INSERT dbo.AuthenticationUsers (Username, PasswordHash, Role)
                VALUES (N'synthetic-sql', N'synthetic-preserved-hash', N'Admin');
                INSERT dbo.AnalysisDashboards (OwnerKey, Name, DocumentJson, DocumentVersion, Revision, CreatedAt, UpdatedAt)
                VALUES (N'original-owner', N'Original', N'{"original":true}', 2, 9, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
                """);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ExecuteAsync(connection.ConnectionString, """
                IF NOT EXISTS (SELECT 1 FROM dbo.ScimUserStates WHERE IsDeleted = 0 AND IsActive = 0 AND Revision = 3)
                    THROW 51001, 'The upgrade changed the source status or did not default IsDeleted to false.', 1;
                IF (SELECT COUNT(*) FROM dbo.DirectoryUsers) <> 1
                    OR NOT EXISTS (SELECT 1 FROM dbo.DirectoryUsers WHERE UserName = N'original@contoso.test' AND UsesStableIdentity = 1 AND Revision = 7)
                    THROW 51002, 'The upgrade changed an existing identity.', 1;
                IF NOT EXISTS (SELECT 1 FROM dbo.AuthenticationUsers WHERE PasswordHash = N'synthetic-preserved-hash' AND Role = N'Admin')
                    OR NOT EXISTS (SELECT 1 FROM dbo.AnalysisDashboards WHERE OwnerKey = N'original-owner' AND Revision = 9 AND DocumentJson = N'{"original":true}')
                    THROW 51003, 'The upgrade changed credentials or ownership.', 1;
                IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261110192113_AddScimUserLifecycle') <> 1
                    THROW 51004, 'The upgrade is not idempotent.', 1;
                """);
            using (var pool = new SqlConnection(connection.ConnectionString)) SqlConnection.ClearPool(pool);
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; CREATE DATABASE [{database}];");
            await ScriptAsync(connection.ConnectionString, section);
            await ScriptAsync(connection.ConnectionString, section);
            await ExecuteAsync(connection.ConnectionString, """
                IF COL_LENGTH('dbo.ScimUserStates', 'IsDeleted') IS NULL
                    OR (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261110192113_AddScimUserLifecycle') <> 1
                    THROW 51005, 'Fresh installation is incomplete or not idempotent.', 1;
                IF EXISTS (SELECT 1 FROM dbo.DirectoryUsers) OR EXISTS (SELECT 1 FROM dbo.ScimConnections)
                    THROW 51006, 'Installation unexpectedly provisioned users or sources.', 1;
                """);
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString,
                    $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    [LocalScimSqlFact]
    public async Task IsolatedSqlServerPreservesIdentityUnderParallelProvisioningAndConditionalUpdates()
    {
        var database = "MonitoringApp_SCIM_AP4_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            var options = new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer(connection.ConnectionString, sql => sql.EnableRetryOnFailure()).Options;
            await using var host = await ScimTestHost.CreateAsync(databaseOptions: options);
            var external = Guid.NewGuid();
            var payload = ScimProtocolTests.User(external, "parallel@contoso.test");
            var responses = await Task.WhenAll(
                host.Client.PostAsJsonAsync("/scim/v2/Users", payload),
                host.Client.PostAsJsonAsync("/scim/v2/Users", payload));
            ScimUserResponse user;
            try
            {
                Assert.Equal(new[] { 201, 409 }, responses.Select(response => (int)response.StatusCode).Order().ToArray());
                user = (await responses.Single(response => response.StatusCode == HttpStatusCode.Created)
                    .Content.ReadFromJsonAsync<ScimUserResponse>())!;
            }
            finally { foreach (var response in responses) response.Dispose(); }

            var updates = await Task.WhenAll(UpdateAsync("First update"), UpdateAsync("Second update"));
            try { Assert.Equal(new[] { 200, 412 }, updates.Select(response => (int)response.StatusCode).Order().ToArray()); }
            finally { foreach (var response in updates) response.Dispose(); }
            var current = (await host.Client.GetFromJsonAsync<ScimUserResponse>("/scim/v2/Users/" + user.Id))!;
            Assert.Contains(current.Name.GivenName, new[] { "First update", "Second update" });
            Assert.Equal("Last", current.Name.FamilyName);
            Assert.Equal(user.Id, current.Id);
            Assert.NotEqual(user.Meta.Version, current.Meta.Version);
            await using (var context = host.CreateDbContext())
            {
                Assert.Equal(1, await context.DirectoryUsers.CountAsync());
                Assert.Equal(1, await context.EntraUserIdentities.CountAsync());
                Assert.Equal(1, await context.ScimUserStates.CountAsync());
                Assert.Empty(await context.AuthenticationUsers.ToArrayAsync());
            }
            using (var deleted = await host.Client.DeleteAsync("/scim/v2/Users/" + user.Id))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using (var missing = await host.Client.GetAsync("/scim/v2/Users/" + user.Id))
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var restored = await host.Client.PostAsJsonAsync("/scim/v2/Users", payload);
            Assert.Equal(HttpStatusCode.Created, restored.StatusCode);
            Assert.Equal(user.Id, (await restored.Content.ReadFromJsonAsync<ScimUserResponse>())!.Id);

            async Task<HttpResponseMessage> UpdateAsync(string name)
            {
                using var request = new HttpRequestMessage(HttpMethod.Patch, "/scim/v2/Users/" + user.Id)
                {
                    Content = JsonContent.Create(ScimProtocolTests.Patch(JsonSerializer.Serialize(new[]
                    {
                        new { op = "replace", path = "name.givenName", value = name }
                    })))
                };
                request.Headers.TryAddWithoutValidation("If-Match", user.Meta.Version);
                return await host.Client.SendAsync(request);
            }
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString,
                    $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    [LocalScimSqlFact]
    public async Task IsolatedAp4AdministrationUpgradeFreshInstallAndConcurrentEditsPreserveExistingData()
    {
        var database = "MonitoringApp_SCIM_AP5_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var scripts = Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts");
        var template = File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql"));
        var upgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeIdentityAdministration.sql")).Trim();
        var section = template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..].Replace("<database-name>", database);
        Assert.Contains(upgrade, section);
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            var groupUpgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeScimGroupLifecycle.sql")).Trim();
            var openRolesUpgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeOpenAdministrationAndTeamInventoryRoles.sql")).Trim();
            await ScriptAsync(connection.ConnectionString, section.Replace(upgrade, "").Replace(groupUpgrade, "").Replace(openRolesUpgrade, ""));
            await ExecuteAsync(connection.ConnectionString, """
                IF OBJECT_ID('dbo.IdentityAdministrationRecords') IS NOT NULL OR COL_LENGTH('dbo.ScimUserStates', 'IsDeleted') IS NULL
                    THROW 51010, 'Expected the AP4 baseline.', 1;
                INSERT dbo.AuthenticationUsers (Username, PasswordHash, Role)
                VALUES (N'preserved-sql', N'synthetic-preserved-hash', N'Admin');
                INSERT dbo.DirectoryUsers (Id, UserName, DisplayName, LocalDisplayName, Origin, IsEnabled, UsesStableIdentity, Revision, CreatedAt, UpdatedAt)
                VALUES ('03000000-0000-0000-0000-000000000001', N'preserved-sql', N'Original', N'Local override', N'Sql', 1, 1, 7,
                    SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
                INSERT dbo.SqlUserIdentities (UserId, Username)
                VALUES ('03000000-0000-0000-0000-000000000001', N'preserved-sql');
                INSERT dbo.UserTeams (Id, Name, IsEnabled, Revision)
                VALUES ('04000000-0000-0000-0000-000000000001', N'Preserved team', 0, 3);
                INSERT dbo.UserTeamMemberships (Id, UserId, TeamId)
                VALUES ('05000000-0000-0000-0000-000000000001', '03000000-0000-0000-0000-000000000001', '04000000-0000-0000-0000-000000000001');
                INSERT dbo.AnalysisDashboards (OwnerKey, Name, DocumentJson, DocumentVersion, Revision, CreatedAt, UpdatedAt)
                VALUES (N'old-owner', N'Original', N'{"original":true}', 2, 9, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
                """);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ExecuteAsync(connection.ConnectionString, """
                IF (SELECT COUNT(*) FROM dbo.DirectoryUsers) <> 1
                    OR NOT EXISTS (SELECT 1 FROM dbo.DirectoryUsers WHERE UserName = N'preserved-sql' AND UsesStableIdentity = 1 AND Revision = 7 AND LocalDisplayName = N'Local override')
                    OR NOT EXISTS (SELECT 1 FROM dbo.AuthenticationUsers WHERE PasswordHash = N'synthetic-preserved-hash' AND Role = N'Admin')
                    OR NOT EXISTS (SELECT 1 FROM dbo.AnalysisDashboards WHERE OwnerKey = N'old-owner' AND Revision = 9 AND DocumentJson = N'{"original":true}')
                    OR NOT EXISTS (SELECT 1 FROM dbo.UserTeams WHERE Name = N'Preserved team' AND IsEnabled = 0 AND Revision = 3)
                    OR (SELECT COUNT(*) FROM dbo.UserTeamMemberships) <> 1
                    THROW 51011, 'The upgrade changed existing identity, teams or ownership.', 1;
                IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261110200132_AddIdentityAdministration') <> 1
                    OR EXISTS (SELECT 1 FROM dbo.IdentityAdministrationRecords)
                    THROW 51012, 'The upgrade was not additive and idempotent.', 1;
                IF (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('dbo.IdentityAdministrationRecords') AND delete_referential_action = 0) <> 3
                    THROW 51013, 'Audit references must not cascade.', 1;
                BEGIN TRY
                    INSERT dbo.IdentityAdministrationRecords (Id, ActorUserId, Action, OccurredAt, BeforeJson, AfterJson)
                    VALUES (NEWID(), '03000000-0000-0000-0000-000000000001', N'Invalid', SYSDATETIMEOFFSET(), N'null', N'null');
                    THROW 51014, 'Missing audit target was accepted.', 1;
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() <> 547 THROW;
                END CATCH;
                """);
            using (var pool = new SqlConnection(connection.ConnectionString)) SqlConnection.ClearPool(pool);
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; CREATE DATABASE [{database}];");
            await ScriptAsync(connection.ConnectionString, section);
            await ScriptAsync(connection.ConnectionString, section);
            var options = new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer(connection.ConnectionString, sql => sql.EnableRetryOnFailure()).Options;
            await using var host = await ScimTestHost.CreateAsync(databaseOptions: options, authentication: "entra");
            var directoryOptions = new IdentityDirectoryOptions { Enabled = true };
            var authentication = new ApplicationAuthenticationOptions { Type = "entra" };
            var directory = new IdentityDirectoryService(host, directoryOptions, authentication);
            var sqlService = new SqlAuthenticationService(host, new SqlPasswordHasher(), directoryOptions);
            var service = new UserAdministrationService(host, directory, authentication, sqlService,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UserAdministrationService>.Instance);
            await using (var context = host.CreateDbContext())
            {
                Assert.Empty(await context.DirectoryUsers.ToArrayAsync());
                Assert.Empty(await context.IdentityAdministrationRecords.ToArrayAsync());
                context.AuthenticationUsers.Add(new() { Username = "native-reader", PasswordHash = "preserved-hash", Role = "Reader" });
                await context.SaveChangesAsync();
            }
            var actor = new ClaimsPrincipal(new ClaimsIdentity([
                new("tid", host.Options.TenantId.ToString()), new("oid", Guid.NewGuid().ToString()),
                new("preferred_username", "native-admin@example.test"), new("name", "Native administrator"),
                new(ClaimTypes.Name, "Native administrator"), new(ClaimTypes.Role, "Admin")
            ], AppServiceAuthenticationHandler.SchemeName));
            var snapshot = await service.GetSnapshotAsync(actor);
            var target = snapshot.Users.Single(user => user.SqlUsername == "native-reader");
            async Task<UserAdministrationException?> ChangeAsync(string displayName)
            {
                try
                {
                    await service.SaveUserAsync(actor, new(target.Key, target.Version, displayName, true, [], SqlRole: "Operator"));
                    return null;
                }
                catch (UserAdministrationException exception) { return exception; }
            }
            var results = await Task.WhenAll(ChangeAsync("First editor"), ChangeAsync("Second editor"));
            Assert.Single(results, result => result is null);
            Assert.Equal("UserDirectory.Errors.Stale", Assert.Single(results, result => result is not null)!.ResourceKey);
            await using var check = host.CreateDbContext();
            Assert.Single(await check.SqlUserIdentities.ToArrayAsync());
            var audit = Assert.Single(await check.IdentityAdministrationRecords.ToArrayAsync());
            Assert.Contains("Operator", audit.AfterJson);
            Assert.DoesNotContain("preserved-hash", audit.BeforeJson + audit.AfterJson);
            Assert.Equal("preserved-hash", (await check.AuthenticationUsers.SingleAsync()).PasswordHash);
            await service.CreateSqlUserAsync(actor, "native-admin-one", "Synthetic-Password-123!", "Admin", new("Admin one", null, null, null), null, []);
            await service.CreateSqlUserAsync(actor, "native-admin-two", "Synthetic-Password-123!", "Admin", new("Admin two", null, null, null), null, []);
            var admins = await service.GetSnapshotAsync(actor);
            var secondAdmin = admins.Users.Single(user => user.SqlUsername == "native-admin-two");
            await service.SaveUserAsync(actor, new(secondAdmin.Key, secondAdmin.Version, null, false, []));
            var firstAdmin = (await service.GetSnapshotAsync(actor)).Users.Single(user => user.SqlUsername == "native-admin-one");
            var lastAdmin = await Assert.ThrowsAsync<UserAdministrationException>(() =>
                service.SaveUserAsync(actor, new(firstAdmin.Key, firstAdmin.Version, null, true, [], SqlRole: "Reader")));
            Assert.Equal("UserDirectory.Errors.LastEffectiveAdmin", lastAdmin.ResourceKey);
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString,
                    $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    [LocalScimSqlFact]
    public async Task IsolatedAp5GroupUpgradePreservesSourcesAndNativeConcurrentGroupUpdatesAreAtomic()
    {
        var database = "MonitoringApp_SCIM_AP6_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var scripts = Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts");
        var template = File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql"));
        var upgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeScimGroupLifecycle.sql")).Trim();
        var openRolesUpgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeOpenAdministrationAndTeamInventoryRoles.sql")).Trim();
        var section = template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..].Replace("<database-name>", database);
        Assert.Contains(upgrade, section);
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            await ScriptAsync(connection.ConnectionString, section.Replace(upgrade, "").Replace(openRolesUpgrade, ""));
            await ExecuteAsync(connection.ConnectionString, """
                IF COL_LENGTH('dbo.ScimGroups','IsDeleted') IS NOT NULL OR COL_LENGTH('dbo.ScimGroupMembers','IsDeleted') IS NOT NULL
                    THROW 51020, 'Expected the AP5 baseline.', 1;
                INSERT dbo.ScimConnections (Id,Name,TenantId,IsEnabled,Revision)
                VALUES ('01000000-0000-0000-0000-000000000001',N'Original source','02000000-0000-0000-0000-000000000001',1,1);
                INSERT dbo.DirectoryUsers (Id,UserName,DisplayName,Origin,IsEnabled,UsesStableIdentity,Revision,CreatedAt,UpdatedAt)
                VALUES ('03000000-0000-0000-0000-000000000001',N'original@example.test',N'Original',N'Scim',1,1,9,SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET());
                INSERT dbo.EntraUserIdentities (UserId,TenantId,ObjectId)
                VALUES ('03000000-0000-0000-0000-000000000001','02000000-0000-0000-0000-000000000001','04000000-0000-0000-0000-000000000001');
                INSERT dbo.ScimUserStates (UserId,ConnectionId,TenantId,ExternalId,IsActive,IsDeleted,SyncStatus,Revision)
                VALUES ('03000000-0000-0000-0000-000000000001','01000000-0000-0000-0000-000000000001',
                    '02000000-0000-0000-0000-000000000001','04000000-0000-0000-0000-000000000001',1,0,N'Succeeded',2);
                INSERT dbo.ScimGroups (Id,ConnectionId,ExternalId,DisplayName,IsActive,SyncStatus,Revision)
                VALUES ('05000000-0000-0000-0000-000000000001','01000000-0000-0000-0000-000000000001',
                    '05000000-0000-0000-0000-000000000002',N'Original group',1,N'Succeeded',7);
                INSERT dbo.ScimGroupMembers (GroupId,UserId,ConnectionId)
                VALUES ('05000000-0000-0000-0000-000000000001','03000000-0000-0000-0000-000000000001','01000000-0000-0000-0000-000000000001');
                INSERT dbo.UserTeams (Id,Name,IsEnabled,Revision)
                VALUES ('06000000-0000-0000-0000-000000000001',N'Original team',1,3);
                INSERT dbo.UserTeamGroupMappings (Id,TeamId,GroupId,IsEnabled,Revision)
                VALUES ('07000000-0000-0000-0000-000000000001','06000000-0000-0000-0000-000000000001','05000000-0000-0000-0000-000000000001',1,2);
                INSERT dbo.UserTeamMemberships (Id,UserId,TeamId,GroupMappingId,SourceGroupId)
                VALUES ('08000000-0000-0000-0000-000000000001','03000000-0000-0000-0000-000000000001','06000000-0000-0000-0000-000000000001',NULL,NULL),
                    ('09000000-0000-0000-0000-000000000001','03000000-0000-0000-0000-000000000001','06000000-0000-0000-0000-000000000001',
                    '07000000-0000-0000-0000-000000000001','05000000-0000-0000-0000-000000000001');
                """);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ExecuteAsync(connection.ConnectionString, """
                IF NOT EXISTS (SELECT 1 FROM dbo.ScimGroups WHERE IsDeleted=0 AND Revision=7 AND DisplayName=N'Original group')
                    OR NOT EXISTS (SELECT 1 FROM dbo.ScimGroupMembers WHERE IsDeleted=0)
                    OR (SELECT COUNT(*) FROM dbo.UserTeamMemberships)<>2
                    OR NOT EXISTS (SELECT 1 FROM dbo.DirectoryUsers WHERE UsesStableIdentity=1 AND Revision=9)
                    OR NOT EXISTS (SELECT 1 FROM dbo.UserTeamGroupMappings WHERE IsEnabled=1 AND Revision=2)
                    OR (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'20261111050629_AddScimGroupLifecycle')<>1
                    THROW 51021, 'The upgrade changed existing membership, mapping or identity data.', 1;
                """);
            await ScriptAsync(connection.ConnectionString, openRolesUpgrade);
            var options = new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer(connection.ConnectionString, sql => sql.EnableRetryOnFailure()).Options;
            await using (var host = await ScimTestHost.CreateAsync(databaseOptions: options, authentication: "entra"))
            {
                var first = await ScimGroupTests.UserAsync(host);
                var second = await ScimGroupTests.UserAsync(host);
                var group = await ScimGroupTests.GroupAsync(host, "Native group");
                var service = ScimGroupTests.Administration(host);
                var actor = ScimGroupTests.Admin(host);
                await service.SaveTeamAsync(actor, null, null, "Native team", true);
                var team = (await service.GetSnapshotAsync(actor)).Teams.Single(team => team.Name == "Native team");
                await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true);
                async Task<HttpStatusCode> AddAsync(string userId)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Patch, "/scim/v2/Groups/" + group.Id)
                    {
                        Content = JsonContent.Create(ScimProtocolTests.Patch($$"""[{"op":"add","path":"members","value":[{"value":"{{userId}}"}]}]"""))
                    };
                    request.Headers.TryAddWithoutValidation("If-Match", group.Meta.Version);
                    using var response = await host.Client.SendAsync(request);
                    return response.StatusCode;
                }
                var results = await Task.WhenAll(AddAsync(first.Id), AddAsync(second.Id));
                Assert.Single(results, status => status == HttpStatusCode.OK);
                Assert.Single(results, status => status == HttpStatusCode.PreconditionFailed);
                var updated = (await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + group.Id))!;
                var winner = Assert.Single(updated.Members).Value;
                var account = (await service.GetSnapshotAsync(actor)).Users.Single(user => user.Id == Guid.Parse(winner));
                await service.SaveUserAsync(actor, new(account.Key, account.Version, null, true, [team.Id]));
                using var deleted = await host.Client.DeleteAsync("/scim/v2/Users/" + winner);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                Assert.Empty((await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + group.Id))!.Members);
                var retained = (await service.GetSnapshotAsync(actor)).Users.Single(user => user.Id == Guid.Parse(winner));
                Assert.True(Assert.Single(retained.TeamSources).Manual);
                await using var check = host.CreateDbContext();
                Assert.True((await check.ScimGroupMembers.SingleAsync(member => member.GroupId == Guid.Parse(group.Id))).IsDeleted);
                Assert.Equal(2, await check.UserTeamMemberships.CountAsync(row => row.UserId == Guid.Parse("03000000-0000-0000-0000-000000000001")));
            }
            using (var pool = new SqlConnection(connection.ConnectionString)) SqlConnection.ClearPool(pool);
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; CREATE DATABASE [{database}];");
            await ScriptAsync(connection.ConnectionString, section);
            await ScriptAsync(connection.ConnectionString, section);
            await ExecuteAsync(connection.ConnectionString, """
                IF COL_LENGTH('dbo.ScimGroups','IsDeleted') IS NULL OR COL_LENGTH('dbo.ScimGroupMembers','IsDeleted') IS NULL
                    OR (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'20261111050629_AddScimGroupLifecycle')<>1
                    OR EXISTS (SELECT 1 FROM dbo.DirectoryUsers) OR EXISTS (SELECT 1 FROM dbo.ScimGroups)
                    THROW 51022, 'Fresh installation was incomplete or unexpectedly provisioned identities.', 1;
                """);
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString,
                    $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    [LocalScimSqlFact]
    public async Task IsolatedNativeRcaDirectoryAssignmentsAndReadLabelsPreserveIdentityAndOwnership()
    {
        var database = "MonitoringApp_SCIM_AP7_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var template = File.ReadAllText(Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts", "CreateMonitoringDatabase.template.sql"));
        var section = template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..].Replace("<database-name>", database);
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            await ScriptAsync(connection.ConnectionString, section);
            var options = new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer(connection.ConnectionString, sql => sql.EnableRetryOnFailure()).Options;
            await using var host = await ScimTestHost.CreateAsync(databaseOptions: options, authentication: "entra");
            await ScimRcaTests.AssertStableAssignmentAsync(host);
            host.Options.Enabled = true;
            await ScimRcaTests.AssertReadLabelsAsync(host);
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString,
                    $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    [LocalScimSqlFact]
    public async Task IsolatedOpenAdministrationUpgradePreservesAuditAndTeamRolesHaveAtomicNativeRevisions()
    {
        var database = "MonitoringApp_SCIM_AP9_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var scripts = Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts");
        var template = File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql"));
        var upgrade = File.ReadAllText(Path.Combine(scripts, "UpgradeOpenAdministrationAndTeamInventoryRoles.sql")).Trim();
        var section = template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..].Replace("<database-name>", database);
        Assert.Contains(upgrade, section);
        var created = false;
        try
        {
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            await ScriptAsync(connection.ConnectionString, section.Replace(upgrade, ""));
            await ExecuteAsync(connection.ConnectionString, """
                IF COL_LENGTH('dbo.IdentityAdministrationRecords','IsOpenAuthentication') IS NOT NULL
                    OR OBJECT_ID('dbo.UserTeamInventoryRoles') IS NOT NULL
                    THROW 51031, 'Expected the AP8 schema.', 1;
                INSERT dbo.AuthenticationUsers (Username,PasswordHash,Role)
                VALUES (N'preserved',N'synthetic-preserved-hash',N'Admin');
                INSERT dbo.DirectoryUsers (Id,UserName,DisplayName,Origin,IsEnabled,UsesStableIdentity,Revision,CreatedAt,UpdatedAt)
                VALUES ('03000000-0000-0000-0000-000000000001',N'preserved',N'Preserved',N'Sql',1,1,7,SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET());
                INSERT dbo.SqlUserIdentities (UserId,Username)
                VALUES ('03000000-0000-0000-0000-000000000001',N'preserved');
                INSERT dbo.UserTeams (Id,Name,IsEnabled,Revision)
                VALUES ('06000000-0000-0000-0000-000000000001',N'Preserved team',1,3);
                INSERT dbo.UserTeamMemberships (Id,UserId,TeamId)
                VALUES (NEWID(),'03000000-0000-0000-0000-000000000001','06000000-0000-0000-0000-000000000001');
                INSERT dbo.IdentityAdministrationRecords (Id,ActorUserId,TargetTeamId,Action,OccurredAt,BeforeJson,AfterJson)
                VALUES (NEWID(),'03000000-0000-0000-0000-000000000001','06000000-0000-0000-0000-000000000001',
                    N'TeamCreated',SYSDATETIMEOFFSET(),N'null',N'{"preserved":true}');
                """);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ScriptAsync(connection.ConnectionString, upgrade);
            await ExecuteAsync(connection.ConnectionString, """
                IF NOT EXISTS (SELECT 1 FROM dbo.IdentityAdministrationRecords WHERE ActorUserId='03000000-0000-0000-0000-000000000001'
                    AND IsOpenAuthentication=0 AND AfterJson=N'{"preserved":true}')
                    OR NOT EXISTS (SELECT 1 FROM dbo.DirectoryUsers WHERE UsesStableIdentity=1 AND Revision=7)
                    OR NOT EXISTS (SELECT 1 FROM dbo.AuthenticationUsers WHERE PasswordHash=N'synthetic-preserved-hash')
                    OR NOT EXISTS (SELECT 1 FROM dbo.UserTeams WHERE Revision=3)
                    OR (SELECT COUNT(*) FROM dbo.UserTeamMemberships)<>1
                    OR (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'20261111080329_AddOpenAdministrationAndTeamInventoryRoles')<>1
                    THROW 51032, 'The upgrade changed historical data or was not idempotent.', 1;
                BEGIN TRY
                    INSERT dbo.IdentityAdministrationRecords (Id,IsOpenAuthentication,TargetTeamId,Action,OccurredAt,BeforeJson,AfterJson)
                    VALUES (NEWID(),0,'06000000-0000-0000-0000-000000000001',N'Invalid',SYSDATETIMEOFFSET(),N'null',N'null');
                    THROW 51033, 'Unauthenticated audit without Open provenance was accepted.', 1;
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER()<>547 THROW;
                END CATCH;
                """);
            var options = new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer(connection.ConnectionString, sql => sql.EnableRetryOnFailure()).Options;
            await using (var host = await ScimTestHost.CreateAsync(databaseOptions: options, authentication: "open"))
            {
                var auth = new ApplicationAuthenticationOptions { Type = "open" };
                var directoryOptions = new IdentityDirectoryOptions { Enabled = true };
                var directory = new IdentityDirectoryService(host, directoryOptions, auth);
                var service = new UserAdministrationService(host, directory, auth, new(host, new(), directoryOptions),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<UserAdministrationService>.Instance);
                var actor = new ClaimsPrincipal(new ClaimsIdentity());
                await using var context = host.CreateDbContext();
                context.ComputerInventory.AddRange(
                    new ComputerInventoryEntry { SubscriptionId = "sub", Computer = "one", Role = "Cafe" },
                    new ComputerInventoryEntry { SubscriptionId = "sub", Computer = "two", Role = "Caf\u00e9" },
                    new ComputerInventoryEntry { SubscriptionId = "sub", Computer = "max", Role = new('r', 256) });
                await context.SaveChangesAsync();
                var team = (await service.GetSnapshotAsync(actor)).Teams.Single();
                await service.SaveTeamAsync(actor, team.Id, team.Revision, team.Name, true,
                    inventoryRoles: [" cafe ", "CAF\u00c9", new('r', 256)]);
                team = (await service.GetSnapshotAsync(actor)).Teams.Single();
                Assert.Equal(3, team.InventoryRoleKeys.Count);
                async Task<UserAdministrationException?> ChangeAsync(string name, string role)
                {
                    try
                    {
                        await service.SaveTeamAsync(actor, team.Id, team.Revision, name, true, inventoryRoles: [role]);
                        return null;
                    }
                    catch (UserAdministrationException exception) { return exception; }
                }
                var results = await Task.WhenAll(ChangeAsync("First", "Cafe"), ChangeAsync("Second", "Caf\u00e9"));
                Assert.Single(results, result => result is null);
                Assert.Equal("UserDirectory.Errors.Stale", Assert.Single(results, result => result is not null)!.ResourceKey);
                var saved = (await service.GetSnapshotAsync(actor)).Teams.Single();
                Assert.Single(saved.InventoryRoleKeys);
                Assert.Equal(team.Revision + 1, saved.Revision);
                Assert.Equal(2, await context.IdentityAdministrationRecords.CountAsync(record => record.IsOpenAuthentication && record.ActorUserId == null));
                Assert.Equal(1, await context.DirectoryUsers.CountAsync());
                Assert.Single(await context.UserTeamMemberships.ToArrayAsync());
            }
            using (var pool = new SqlConnection(connection.ConnectionString)) SqlConnection.ClearPool(pool);
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; CREATE DATABASE [{database}];");
            await ScriptAsync(connection.ConnectionString, section);
            await ScriptAsync(connection.ConnectionString, section);
            await ExecuteAsync(connection.ConnectionString, """
                IF COL_LENGTH('dbo.IdentityAdministrationRecords','IsOpenAuthentication') IS NULL
                    OR OBJECT_ID('dbo.UserTeamInventoryRoles') IS NULL
                    OR EXISTS (SELECT 1 FROM dbo.DirectoryUsers)
                    OR EXISTS (SELECT 1 FROM dbo.IdentityAdministrationRecords)
                    THROW 51034, 'Fresh installation was incomplete or created fake identities.', 1;
                """);
        }
        finally
        {
            if (created)
            {
                using var pool = new SqlConnection(connection.ConnectionString);
                SqlConnection.ClearPool(pool);
                await ExecuteAsync(master.ConnectionString,
                    $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ScriptAsync(string connectionString, string script)
    {
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(batch => !string.IsNullOrWhiteSpace(batch)))
            await ExecuteAsync(connectionString, batch);
    }

    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));

    private sealed class LocalScimSqlFactAttribute : FactAttribute
    {
        public LocalScimSqlFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MONITORINGAPP_SCIM_SQL_TESTS") != "1")
                Skip = "Opt-in isolated LocalDB test: set MONITORINGAPP_SCIM_SQL_TESTS=1 on Windows.";
        }
    }
}
