using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class IdentityTransitionTests
{
    private static readonly Guid Tenant = Guid.Parse("01000000-0000-0000-0000-000000000001");
    private static readonly Guid AdminObject = Guid.Parse("02000000-0000-0000-0000-000000000001");
    private static readonly Guid UserObject = Guid.Parse("02000000-0000-0000-0000-000000000002");
    private static ClaimsPrincipal Admin => Entra(AdminObject, "admin@contoso.test", "Admin");
    private static ClaimsPrincipal Operator => Entra(UserObject, "operator@contoso.test", "Operator");
    private static readonly string[] Aliases = ["operator@contoso.test", "old@contoso.test", UserObject.ToString("D")];

    [Fact]
    public async Task DisabledFeatureUsesNoDatabaseAndDoesNotTrustSuppliedInternalActorClaims()
    {
        var factory = new FailingFactory();
        var directory = new IdentityDirectoryService(factory, new(), new() { Type = "entra" });
        var user = Entra(UserObject, "operator@contoso.test", "Operator");
        user.AddIdentity(new ClaimsIdentity([
            new(DirectoryIdentity.ActorClaim, "user:forged", ClaimValueTypes.String, DirectoryIdentity.Issuer)
        ], DirectoryIdentity.AuthenticationType));
        var resolved = await directory.ResolveAsync(user);
        Assert.Null(DirectoryIdentity.Actor(resolved));
        Assert.Equal(0, factory.Calls);
        var identity = new CurrentUserIdentity(new HttpContextAccessor(), directory);
        Assert.False(identity.DirectoryEnabled);
        Assert.Equal("operator@contoso.test", await identity.GetActorAsync(user, false));
    }

    [Fact]
    public async Task FirstLoginRecordsStablePairWithoutProvisioningAndKeepsLegacyOwnershipUntilApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolved = await fixture.Directory.ResolveAsync(Operator);
        var id = Guid.Parse(resolved.FindFirstValue(DirectoryIdentity.UserIdClaim)!);
        Assert.Null(DirectoryIdentity.Actor(resolved));
        var renamed = Entra(UserObject, "new-login@contoso.test", "Operator");
        var again = await fixture.Directory.ResolveAsync(renamed);
        Assert.Equal(id.ToString("D"), again.FindFirstValue(DirectoryIdentity.UserIdClaim));
        await using var context = fixture.CreateDbContext();
        var user = await context.DirectoryUsers.SingleAsync();
        Assert.Equal("new-login@contoso.test", user.UserName);
        Assert.Null(user.Email);
        Assert.False(user.UsesStableIdentity);
        Assert.Empty(await context.ScimUserStates.ToArrayAsync());
        Assert.Empty(await context.ScimConnections.ToArrayAsync());
        Assert.Empty(await context.UserTeamMemberships.ToArrayAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InvalidOrUnauthorizedPrincipalNeverCreatesAUser(int scenario)
    {
        await using var fixture = await Fixture.CreateAsync();
        var identity = new ClaimsIdentity(Operator.Identities.Single());
        if (scenario == 0) identity.RemoveClaim(identity.FindFirst("tid")!);
        if (scenario == 1) identity.RemoveClaim(identity.FindFirst("oid")!);
        if (scenario == 2) identity.AddClaim(new("idtyp", "app"));
        if (scenario == 3) identity.RemoveClaim(identity.FindFirst(ClaimTypes.Role)!);
        if (scenario == 4) identity.AddClaim(new("http://schemas.microsoft.com/identity/claims/tenantid", Guid.NewGuid().ToString()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Directory.ResolveAsync(new(identity)));
        await using var context = fixture.CreateDbContext();
        Assert.Empty(await context.DirectoryUsers.ToArrayAsync());
    }

    [Fact]
    public async Task SameEmailWithDifferentObjectOrTenantIsNeverMerged()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Directory.ResolveAsync(Operator);
        await fixture.Directory.ResolveAsync(Entra(Guid.NewGuid(), "operator@contoso.test", "Operator"));
        var otherTenant = Entra(UserObject, "operator@contoso.test", "Operator", Guid.NewGuid());
        await fixture.Directory.ResolveAsync(otherTenant);
        await using var context = fixture.CreateDbContext();
        Assert.Equal(3, await context.DirectoryUsers.CountAsync());
        Assert.Equal(3, await context.EntraUserIdentities.CountAsync());
    }

    [Fact]
    public async Task PreviewIsReadOnlyAndApprovalPreservesIdsPublicationsAuditAndOriginalSnapshots()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (userId, first, second) = await fixture.SeedAsync();
        var preview = await fixture.Migration.PreviewAsync(Admin, userId, Aliases);
        Assert.Empty(preview.Conflicts);
        Assert.Equal((2, 2, 1, 1, 1), (preview.DashboardCollections, preview.Dashboards, preview.InboxLayouts, preview.Publications, preview.Jobs));
        await using (var context = fixture.CreateDbContext())
        {
            Assert.False((await context.DirectoryUsers.FindAsync(userId))!.UsesStableIdentity);
            Assert.Empty(await context.LegacyUserLinks.ToArrayAsync());
            Assert.Empty(await context.IdentityMigrationRecords.ToArrayAsync());
        }
        await fixture.Migration.CommitAsync(Admin, userId, Aliases, preview.Fingerprint, "Verified against tenant/object identity");
        await fixture.Migration.CommitAsync(Admin, userId, Aliases, preview.Fingerprint, "Verified retry");
        var resolved = await fixture.Directory.ResolveAsync(Entra(UserObject, "renamed@contoso.test", "Operator"));
        Assert.Equal(DirectoryIdentity.OwnerKey(userId), DirectoryIdentity.Actor(resolved));
        Assert.Contains("old@contoso.test", DirectoryIdentity.JobActors(resolved));
        await using var verify = fixture.CreateDbContext();
        var stored = await verify.AnalysisDashboards.SingleAsync();
        var merged = AnalysisDashboardCollection.Deserialize(stored.DocumentJson);
        Assert.Equal(new[] { first, second }.Order(), merged.Dashboards.Select(row => row.Id).Order());
        Assert.Equal(10, stored.Revision);
        Assert.Equal(DirectoryIdentity.OwnerKey(userId), stored.OwnerKey);
        Assert.Equal(DirectoryIdentity.OwnerKey(userId), (await verify.AnalysisDashboardTemplates.SingleAsync()).UpdatedBy);
        Assert.Equal(first, (await verify.AnalysisDashboardTemplates.SingleAsync()).Id);
        Assert.Equal(DirectoryIdentity.OwnerKey(userId), (await verify.InboxLayouts.SingleAsync()).OwnerKey);
        Assert.Equal("old@contoso.test", (await verify.LogbookEntries.SingleAsync()).User);
        var job = await verify.AgentAnalysisJobs.SingleAsync();
        Assert.Equal("old-job-key", job.IdempotencyKey);
        Assert.Equal(UserObject.ToString("D"), job.CreatedBy);
        Assert.True(AgentAnalysisJobApi.CanAccess(job, resolved, false));
        var record = await verify.IdentityMigrationRecords.SingleAsync();
        Assert.Contains("old@contoso.test", record.OriginalStateJson);
        Assert.Contains("\"Revision\":9", record.OriginalStateJson);
        Assert.Equal(preview.Fingerprint, record.Fingerprint);
        Assert.Equal(3, await verify.LegacyUserLinks.CountAsync());
    }

    [Fact]
    public async Task StalePreviewCannotCommitOrPartiallyActivate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, _, _) = await fixture.SeedAsync();
        var preview = await fixture.Migration.PreviewAsync(Admin, id, Aliases);
        await using (var context = fixture.CreateDbContext())
        {
            var stored = await context.AnalysisDashboards.FirstAsync();
            stored.Revision++;
            await context.SaveChangesAsync();
        }
        var exception = await Assert.ThrowsAsync<IdentityMigrationException>(() =>
            fixture.Migration.CommitAsync(Admin, id, Aliases, preview.Fingerprint, "verified"));
        Assert.Equal("IdentityMigration.Errors.Stale", exception.ResourceKey);
        await using var verify = fixture.CreateDbContext();
        Assert.False((await verify.DirectoryUsers.FindAsync(id))!.UsesStableIdentity);
        Assert.Empty(await verify.IdentityMigrationRecords.ToArrayAsync());
        Assert.Empty(await verify.LegacyUserLinks.ToArrayAsync());
        Assert.Equal(2, await verify.AnalysisDashboards.CountAsync());
    }

    [Theory]
    [InlineData(0, "IdentityMigration.Conflicts.DashboardId")]
    [InlineData(1, "IdentityMigration.Conflicts.Inbox")]
    [InlineData(2, "IdentityMigration.Conflicts.LegacyJson")]
    [InlineData(3, "IdentityMigration.Conflicts.Publication")]
    [InlineData(4, "IdentityMigration.Conflicts.InvalidJson")]
    [InlineData(5, "IdentityMigration.Conflicts.Identity")]
    [InlineData(6, "IdentityMigration.Conflicts.InvalidJson")]
    [InlineData(7, "IdentityMigration.Conflicts.InvalidJson")]
    public async Task ConflictsBlockApprovalWithoutMutatingExistingCollections(int scenario, string conflict)
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, first, _) = await fixture.SeedAsync();
        await using (var context = fixture.CreateDbContext())
        {
            var other = await context.AnalysisDashboards.SingleAsync(row => row.OwnerKey == "operator@contoso.test");
            if (scenario == 0)
            {
                var collection = AnalysisDashboardCollection.Deserialize(other.DocumentJson);
                collection.Active.Id = first;
                collection.ActiveDashboardId = first;
                other.DocumentJson = collection.Serialize();
            }
            if (scenario == 1) context.InboxLayouts.Add(new() { OwnerKey = "operator@contoso.test", DocumentJson = "{}", Revision = 1 });
            if (scenario == 2) other.DocumentJson = """{"name":"old single document"}""";
            if (scenario == 3) (await context.AnalysisDashboardTemplates.SingleAsync()).UpdatedBy = "another-owner";
            if (scenario == 4) other.DocumentJson = "{";
            if (scenario == 5)
                context.EntraUserIdentities.Remove(await context.EntraUserIdentities.SingleAsync(entry => entry.UserId == id));
            if (scenario is 6 or 7)
            {
                var json = JsonNode.Parse(other.DocumentJson)!;
                if (scenario == 6) json["dashboards"]![0]!.AsObject().Remove("document");
                else json["dashboards"]!.AsArray().Add(JsonNode.Parse("""{"document":{"name":"Missing ID"}}"""));
                other.DocumentJson = json.ToJsonString();
            }
            await context.SaveChangesAsync();
        }
        var preview = await fixture.Migration.PreviewAsync(Admin, id, Aliases);
        Assert.Contains(conflict, preview.Conflicts);
        await Assert.ThrowsAsync<IdentityMigrationException>(() =>
            fixture.Migration.CommitAsync(Admin, id, Aliases, preview.Fingerprint, "verified"));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(2, await verify.AnalysisDashboards.CountAsync());
        Assert.Empty(await verify.IdentityMigrationRecords.ToArrayAsync());
        if (scenario == 5)
            Assert.DoesNotContain(await fixture.Directory.ListAsync(Admin), entry => entry.Id == id);
    }

    [Fact]
    public async Task CachedPrincipalUsesFreshActorAfterApprovalAndCannotWriteAsDisabledUser()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, _, _) = await fixture.SeedAsync();
        var identity = new CurrentUserIdentity(new HttpContextAccessor(), fixture.Directory);
        Assert.True(identity.DirectoryEnabled);
        var principal = Operator;
        Assert.Equal("operator@contoso.test", await identity.GetActorAsync(principal, false));
        var preview = await fixture.Migration.PreviewAsync(Admin, id, Aliases);
        await fixture.Migration.CommitAsync(Admin, id, Aliases, preview.Fingerprint, "verified");
        Assert.Equal(DirectoryIdentity.OwnerKey(id), await identity.GetActorAsync(principal, false));
        await using var context = fixture.CreateDbContext();
        var user = (await context.DirectoryUsers.FindAsync(id))!;
        user.IsEnabled = false;
        user.Revision++;
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.GetActorAsync(principal, false));
    }

    [Fact]
    public async Task OldEditorsCannotRecreateLegacyCollectionsAfterApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, _, _) = await fixture.SeedAsync();
        var options = new IdentityDirectoryOptions { Enabled = true };
        var repository = new AnalysisDashboardRepository(fixture, options);
        var inbox = new InboxLayoutRepository(fixture, directoryOptions: options);
        var draft = (await repository.LoadAsync("old@contoso.test")).Collection!;
        var preview = await fixture.Migration.PreviewAsync(Admin, id, Aliases);
        await fixture.Migration.CommitAsync(Admin, id, Aliases, preview.Fingerprint, "verified");
        await Assert.ThrowsAsync<IdentityMigrationException>(() => repository.SaveAsync("old@contoso.test", draft, 7));
        await Assert.ThrowsAsync<IdentityMigrationException>(() => repository.LoadAsync("old@contoso.test"));
        await Assert.ThrowsAsync<IdentityMigrationException>(() => inbox.SaveAsync("old@contoso.test", new()));
        await Assert.ThrowsAsync<IdentityMigrationException>(() => inbox.LoadAsync("old@contoso.test"));
        var current = await repository.LoadAsync(DirectoryIdentity.OwnerKey(id));
        Assert.Equal(2, current.Collection!.Dashboards.Count);
        await repository.SaveAsync(DirectoryIdentity.OwnerKey(id), current.Collection, current.Revision);
    }

    [Fact]
    public async Task DisabledUsersAndProvisioningDeactivationInvalidateAnExistingPrincipal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolved = await fixture.Directory.ResolveAsync(Operator);
        var id = Guid.Parse(resolved.FindFirstValue(DirectoryIdentity.UserIdClaim)!);
        await using (var context = fixture.CreateDbContext())
        {
            var user = (await context.DirectoryUsers.FindAsync(id))!;
            user.IsEnabled = false;
            user.Revision++;
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Directory.ResolveAsync(resolved));
        await using (var context = fixture.CreateDbContext())
        {
            var user = (await context.DirectoryUsers.FindAsync(id))!;
            user.IsEnabled = true;
            user.Revision++;
            var connection = new ScimConnection { Name = "disabled connection", TenantId = Tenant };
            context.AddRange(connection, new ScimUserState
            {
                UserId = id, ConnectionId = connection.Id, TenantId = Tenant, ExternalId = UserObject, IsActive = false
            });
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Directory.ResolveAsync(resolved));
    }

    [Fact]
    public async Task LoginCannotOverwriteScimProfileOrLocalDisplayName()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolved = await fixture.Directory.ResolveAsync(Operator);
        var id = Guid.Parse(resolved.FindFirstValue(DirectoryIdentity.UserIdClaim)!);
        await using (var context = fixture.CreateDbContext())
        {
            var user = (await context.DirectoryUsers.FindAsync(id))!;
            user.DisplayName = "Provisioned source";
            user.LocalDisplayName = "Local display";
            var connection = new ScimConnection { Name = "source", TenantId = Tenant };
            context.AddRange(connection, new ScimUserState
            {
                UserId = id, ConnectionId = connection.Id, TenantId = Tenant, ExternalId = UserObject
            });
            await context.SaveChangesAsync();
        }
        await fixture.Directory.ResolveAsync(Entra(UserObject, "different-claim@contoso.test", "Operator"));
        await using var verify = fixture.CreateDbContext();
        var stored = (await verify.DirectoryUsers.FindAsync(id))!;
        Assert.Equal("operator@contoso.test", stored.UserName);
        Assert.Equal("Provisioned source", stored.DisplayName);
        Assert.Equal("Local display", stored.LocalDisplayName);
    }

    [Fact]
    public async Task ReusedLoginCannotAccessAnAlreadyAssignedHistoricalOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, _, _) = await fixture.SeedAsync();
        var preview = await fixture.Migration.PreviewAsync(Admin, id, Aliases);
        await fixture.Migration.CommitAsync(Admin, id, Aliases, preview.Fingerprint, "verified");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Directory.ResolveAsync(Entra(Guid.NewGuid(), "old@contoso.test", "Operator")));
    }

    [Fact]
    public async Task NonAdminAndUnverifiedInputCannotCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, _, _) = await fixture.SeedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Migration.PreviewAsync(Operator, id, Aliases));
        await Assert.ThrowsAsync<IdentityMigrationException>(() => fixture.Migration.PreviewAsync(Admin, id, ["System"]));
        var preview = await fixture.Migration.PreviewAsync(Admin, id, ["old@contoso.test"]);
        Assert.Contains("IdentityMigration.Conflicts.MissingAlias", preview.Conflicts);
        await Assert.ThrowsAsync<IdentityMigrationException>(() =>
            fixture.Migration.CommitAsync(Admin, id, Aliases, preview.Fingerprint, ""));
    }

    [Fact]
    public async Task CommitFailureRollsBackOwnershipActivationAliasesAndAuditRecordTogether()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, _, _) = await fixture.SeedAsync();
        var preview = await fixture.Migration.PreviewAsync(Admin, id, Aliases);
        await using (var context = fixture.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                """CREATE TRIGGER RejectApproval BEFORE INSERT ON IdentityMigrationRecords BEGIN SELECT RAISE(ABORT, 'forced write failure'); END;""");
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Migration.CommitAsync(Admin, id, Aliases, preview.Fingerprint, "verified"));
        await using var verify = fixture.CreateDbContext();
        Assert.False((await verify.DirectoryUsers.FindAsync(id))!.UsesStableIdentity);
        Assert.Equal(2, await verify.AnalysisDashboards.CountAsync());
        Assert.Equal("old@contoso.test", (await verify.AnalysisDashboardTemplates.SingleAsync()).UpdatedBy);
        Assert.Equal("old@contoso.test", (await verify.InboxLayouts.SingleAsync()).OwnerKey);
        Assert.Empty(await verify.LegacyUserLinks.ToArrayAsync());
        Assert.Empty(await verify.IdentityMigrationRecords.ToArrayAsync());
    }

    [Fact]
    public async Task ExistingSessionIsRecheckedByApplicationAuthorizationPolicies()
    {
        await using var fixture = await Fixture.CreateAsync();
        var options = new IdentityDirectoryOptions { Enabled = true };
        var authentication = new ApplicationAuthenticationOptions { Type = "entra" };
        using var provider = new ServiceCollection().AddLogging()
            .AddSingleton(options).AddSingleton(authentication).AddSingleton<IDbContextFactory<AlertDbContext>>(fixture)
            .AddScoped<IdentityDirectoryService>().AddScoped<IAuthorizationHandler, DirectoryAccessHandler>()
            .AddAuthorization(configuration => ApplicationAuthorization.Configure(configuration, authentication, options))
            .BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.True((await service.AuthorizeAsync(Operator, ApplicationAuthorization.OperatorPolicy)).Succeeded);
        await using (var context = fixture.CreateDbContext())
        {
            var user = await context.DirectoryUsers.SingleAsync();
            user.IsEnabled = false;
            user.Revision++;
            await context.SaveChangesAsync();
        }
        Assert.False((await service.AuthorizeAsync(Operator, ApplicationAuthorization.OperatorPolicy)).Succeeded);
    }

    [Fact]
    public void ActivationSqlMatchesTheGeneratedUpgradeAndKeepsExistingOwnershipUnchanged()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Integrated Security=True").Options);
        var generated = context.GetService<IMigrator>().GenerateScript(
            "20261110112945_AddIdentityDirectory", "20261110184457_AddIdentityActivation",
            MigrationsSqlGenerationOptions.Idempotent);
        static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var upgrade = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "UpgradeIdentityActivation.sql"));
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        Assert.Equal(Normalize(generated), Normalize(upgrade));
        Assert.Contains(Normalize(generated), Normalize(template));
        Assert.Contains("[UsesStableIdentity] bit NOT NULL DEFAULT CAST(0 AS bit)", generated);
        Assert.DoesNotContain("UPDATE [AnalysisDashboards]", generated);
        Assert.DoesNotContain("INSERT INTO [DirectoryUsers]", generated);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task SqlDeletionDisablesLinkedIdentityAndKeepsLastAdminProtectionAtomic()
    {
        await using var fixture = await Fixture.CreateAsync();
        var options = new IdentityDirectoryOptions { Enabled = true };
        var auth = new ApplicationAuthenticationOptions { Type = "sql" };
        var directory = new IdentityDirectoryService(fixture, options, auth);
        await using (var context = fixture.CreateDbContext())
        {
            context.AuthenticationUsers.AddRange(
                new() { Username = "admin", PasswordHash = "keep", Role = "Admin" },
                new() { Username = "operator", PasswordHash = "keep", Role = "Operator" });
            await context.SaveChangesAsync();
        }
        var admin = Sql("admin", "Admin");
        var op = Sql("operator", "Operator");
        var old = await directory.ResolveAsync(op);
        await directory.ResolveAsync(admin);
        var service = new SqlAuthenticationService(fixture, new SqlPasswordHasher(), options);
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => service.DeleteUserAsync("admin"));
        await using (var context = fixture.CreateDbContext())
            Assert.True((await context.DirectoryUsers.SingleAsync(user => user.UserName == "admin")).IsEnabled);
        Assert.True(await service.DeleteUserAsync("operator"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ResolveAsync(old));
        await using var verify = fixture.CreateDbContext();
        Assert.False((await verify.DirectoryUsers.SingleAsync(user => user.UserName == "operator")).IsEnabled);
        Assert.Single(await verify.SqlUserIdentities.ToArrayAsync());
    }

    [Fact]
    public void DirectoryAuthorizationServicesResolveWithoutCyclesAndAreOptIn()
    {
        var options = new IdentityDirectoryOptions { Enabled = true };
        var authentication = new ApplicationAuthenticationOptions { Type = "entra" };
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(options).AddSingleton(authentication)
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(
                "Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Integrated Security=True", false))
            .AddAuthorization(configuration => ApplicationAuthorization.Configure(configuration, authentication, options))
            .BuildServiceProvider(validateScopes: true);
        using var scope = services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuthorizationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IdentityDirectoryService>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de-DE")]
    public async Task MigrationPanelRendersLocalizedReadOnlyPreviewWithTargetEvidence(string culture)
    {
        await using var fixture = await Fixture.CreateAsync();
        var (userId, _, _) = await fixture.SeedAsync();
        IdentityMigrationPanel? panel = null;
        using var cultureScope = new UiCultureScope(culture);
        var collection = new ServiceCollection().AddLogging().AddSingleton(fixture.Directory)
            .AddSingleton(fixture.Migration);
        collection.AddUiLocalization();
        await using var provider = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Admin)),
                    ["ChildContent"] = (Microsoft.AspNetCore.Components.RenderFragment)(builder =>
                    {
                        builder.OpenComponent<IdentityMigrationPanel>(0);
                        builder.AddComponentReferenceCapture(1, component => panel = (IdentityMigrationPanel)component);
                        builder.CloseComponent();
                    })
                }))).ToHtmlString());
        var text = provider.GetRequiredService<UiText>();
        Assert.Contains(text["IdentityMigration.Title"], System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains(UserObject.ToString("D"), html);
        Assert.Contains("old@contoso.test", html);
        Assert.DoesNotContain("IdentityMigration.", html);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(IdentityMigrationPanel).GetField(name, flags)!.SetValue(panel, value);
            Task Call(string name) => (Task)typeof(IdentityMigrationPanel).GetMethod(name, flags)!.Invoke(panel, [])!;
            Set("targetId", userId);
            Set("aliasText", string.Join('\n', Aliases));
            await Call("PreviewAsync");
            Set("evidence", "Verified tenant and object identity");
            Set("confirmed", true);
            await Call("CommitAsync");
            var accounts = Assert.IsAssignableFrom<IReadOnlyList<DirectoryAccount>>(
                typeof(IdentityMigrationPanel).GetField("accounts", flags)!.GetValue(panel));
            Assert.True(accounts.Single(account => account.Id == userId).UsesStableIdentity);
            Assert.True((bool)typeof(IdentityMigrationPanel).GetField("success", flags)!.GetValue(panel)!);
            Assert.Null(typeof(IdentityMigrationPanel).GetField("error", flags)!.GetValue(panel));
        });
    }

    private static ClaimsPrincipal Entra(Guid objectId, string name, string role, Guid? tenant = null) => new(new ClaimsIdentity([
        new("tid", (tenant ?? Tenant).ToString("D")), new("oid", objectId.ToString("D")),
        new(ClaimTypes.NameIdentifier, objectId.ToString("D")), new("preferred_username", name),
        new(ClaimTypes.Name, name), new(ClaimTypes.Role, role)
    ], AppServiceAuthenticationHandler.SchemeName));

    private static ClaimsPrincipal Sql(string name, string role) => new(new ClaimsIdentity([
        new(ClaimTypes.NameIdentifier, name), new(ClaimTypes.Name, name), new(ClaimTypes.Role, role)
    ], ApplicationAuthorization.SqlCookieScheme));

    private sealed class FailingFactory : IDbContextFactory<AlertDbContext>
    {
        public int Calls { get; private set; }
        public AlertDbContext CreateDbContext() { Calls++; throw new InvalidOperationException("Database must not be used."); }
    }

    private sealed class UiCultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;
        private readonly CultureInfo previousUi = CultureInfo.CurrentUICulture;
        public UiCultureScope(string culture)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        }
        public void Dispose()
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    private sealed class Fixture(SqliteConnection connection, DbContextOptions<AlertDbContext> options)
        : IDbContextFactory<AlertDbContext>, IAsyncDisposable
    {
        public AlertDbContext CreateDbContext() => new(options);
        public IdentityDirectoryService Directory => new(this, new() { Enabled = true }, new() { Type = "entra" });
        public IdentityMigrationService Migration => new(this, Directory);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var fixture = new Fixture(connection, new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = fixture.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            return fixture;
        }

        public async Task<(Guid UserId, Guid First, Guid Second)> SeedAsync()
        {
            var resolved = await Directory.ResolveAsync(Operator);
            var id = Guid.Parse(resolved.FindFirstValue(DirectoryIdentity.UserIdClaim)!);
            var first = AnalysisDashboardCollection.Create(new() { Name = "Old dashboard" });
            var second = AnalysisDashboardCollection.Create(new() { Name = "New dashboard" });
            await using var context = CreateDbContext();
            context.AnalysisDashboards.AddRange(
                new() { OwnerKey = "old@contoso.test", DocumentJson = first.Serialize(), DocumentVersion = 2, Revision = 7 },
                new() { OwnerKey = "operator@contoso.test", DocumentJson = second.Serialize(), DocumentVersion = 2, Revision = 9 });
            context.InboxLayouts.Add(new() { OwnerKey = "old@contoso.test", DocumentJson = "{}", Revision = 5 });
            context.AnalysisDashboardTemplates.Add(new()
            {
                Id = first.Active.Id, UpdatedBy = "old@contoso.test", DocumentJson = JsonSerializer.Serialize(first.Active.Document),
                Published = true, Version = 3
            });
            context.AgentAnalysisJobs.Add(new()
            {
                Id = Guid.NewGuid(), AnalysisId = Guid.NewGuid(), CreatedBy = UserObject.ToString("D"), IdempotencyKey = "old-job-key"
            });
            context.LogbookEntries.Add(new() { Id = Guid.NewGuid(), User = "old@contoso.test", Comment = "History" });
            await context.SaveChangesAsync();
            return (id, first.Active.Id, second.Active.Id);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
