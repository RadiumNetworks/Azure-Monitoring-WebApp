using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class DatabaseResetTests
{
    [Fact]
    public async Task ClearsEveryOperationalTableRestoresDefaultsAndPreservesAccessAndSettings()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var changes = 0;
        fixture.Store.Changed += () => changes++;

        Assert.Equal(10, await fixture.Reset.ResetAsync(User(SqlAuthenticationRoles.Admin), "RESET"));

        await using var context = fixture.Factory.CreateDbContext();
        await AssertOperationalCountsAsync(context, 0);
        var rules = await context.AlertRules.AsNoTracking().OrderBy(rule => rule.Id).ToArrayAsync();
        Assert.Equal(
            JsonSerializer.Serialize(DefaultAlertRules.Create().OrderBy(rule => rule.Id)),
            JsonSerializer.Serialize(rules));
        await AssertPreservedAsync(context);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task InitializesEmptyOperationalTablesAndCanBeRepeated()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(10, await fixture.Reset.ResetAsync(User(SqlAuthenticationRoles.Admin), "RESET"));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var rule = await context.AlertRules.FirstAsync();
            rule.Name = "Changed default";
            rule.Enabled = false;
            await context.SaveChangesAsync();
        }
        Assert.Equal(10, await fixture.Reset.ResetAsync(User(SqlAuthenticationRoles.Admin), "RESET"));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(10, await check.AlertRules.CountAsync());
        Assert.All(await check.AlertRules.ToArrayAsync(), rule => Assert.True(rule.Enabled));
        Assert.False(await check.AlertRules.AnyAsync(rule => rule.Name == "Changed default"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("reset")]
    [InlineData("Reset")]
    [InlineData(" RESET")]
    [InlineData("RESET ")]
    [InlineData("DELETE")]
    public async Task RejectsInexactConfirmationWithoutChangingData(string confirmation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Reset.ResetAsync(User(SqlAuthenticationRoles.Admin), confirmation));
        await using var context = fixture.Factory.CreateDbContext();
        await AssertOriginalDataAsync(context);
    }

    [Theory]
    [InlineData(SqlAuthenticationRoles.Operator)]
    [InlineData(null)]
    public async Task RejectsNonAdministratorsEvenWithExactConfirmation(string? role)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Reset.ResetAsync(User(role), "RESET"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Reset.ResetAsync(new ClaimsPrincipal(new ClaimsIdentity()), "RESET"));
        await using var context = fixture.Factory.CreateDbContext();
        await AssertOriginalDataAsync(context);
    }

    [Fact]
    public async Task HonorsExistingOpenAuthenticationAdminPolicy()
    {
        await using var fixture = await Fixture.CreateAsync(open: true);
        Assert.Equal(10, await fixture.Reset.ResetAsync(new ClaimsPrincipal(new ClaimsIdentity()), "RESET"));
    }

    [Theory]
    [InlineData("CREATE TRIGGER FailReset BEFORE DELETE ON ComputerInventory BEGIN SELECT RAISE(ABORT, 'Delete failed'); END;")]
    [InlineData("CREATE TRIGGER FailReset BEFORE INSERT ON AlertRules BEGIN SELECT RAISE(ABORT, 'Default insert failed'); END;")]
    public async Task RollsBackAllDeletesWhenDeletionOrRestoringDefaultsFails(string trigger)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(trigger);
        var changes = 0;
        fixture.Store.Changed += () => changes++;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Reset.ResetAsync(User(SqlAuthenticationRoles.Admin), "RESET"));

        await using var check = fixture.Factory.CreateDbContext();
        await AssertOriginalDataAsync(check);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task FailedNotificationDoesNotTurnCommittedResetIntoFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var notified = false;
        fixture.Store.Changed += () => throw new InvalidOperationException("Disposed subscriber");
        fixture.Store.Changed += () => notified = true;

        Assert.Equal(10, await fixture.Reset.ResetAsync(User(SqlAuthenticationRoles.Admin), "RESET"));
        Assert.True(notified);
    }

    [Fact]
    public void DefaultRulesMatchEveryFieldOfProvisioningDefaults()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        var inserts = Regex.Matches(script,
            @"INSERT INTO \[dbo\]\.\[AlertRules\]\s*\((?<columns>[^)]+)\)\s*VALUES\s*\((?<values>[^)]+)\);");
        var expected = new List<AlertRule>();
        foreach (Match insert in inserts)
        {
            var columns = insert.Groups["columns"].Value.Split(',').Select(column => column.Trim().Trim('[', ']')).ToArray();
            var values = insert.Groups["values"].Value.Split(',').Select(value => value.Trim()).ToArray();
            Assert.Equal(columns.Length, values.Length);
            var rule = new AlertRule();
            for (var index = 0; index < columns.Length; index++)
            {
                var property = typeof(AlertRule).GetProperty(columns[index]);
                Assert.NotNull(property);
                var value = values[index];
                object parsed = property.PropertyType == typeof(Guid) ? Guid.Parse(value.Trim('\''))
                    : property.PropertyType == typeof(bool) ? value == "1"
                    : property.PropertyType == typeof(int) ? int.Parse(value, CultureInfo.InvariantCulture)
                    : value[(value.StartsWith("N'", StringComparison.Ordinal) ? 2 : 1)..^1].Replace("''", "'");
                property.SetValue(rule, parsed);
            }
            expected.Add(rule);
        }

        Assert.Equal(10, expected.Count);
        Assert.Equal(JsonSerializer.Serialize(expected.OrderBy(rule => rule.Id)),
            JsonSerializer.Serialize(DefaultAlertRules.Create().OrderBy(rule => rule.Id)));
    }

    [Fact]
    public void StandaloneCleanupRestoresExactDefaultsAndOnlyTargetsOperationalTables()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "ResetMonitoringData.sql"));
        var seed = Regex.Match(script, @"EXEC sys\.sp_executesql N'(?<sql>(?:[^']|'')*)';");
        Assert.True(seed.Success);
        var sql = seed.Groups["sql"].Value.Replace("''", "'");
        var insert = Regex.Match(sql, @"INSERT INTO \[dbo\]\.\[AlertRules\]\s*\((?<columns>[^)]+)\)\s*VALUES");
        Assert.True(insert.Success);
        var columns = insert.Groups["columns"].Value.Split(',').Select(column => column.Trim().Trim('[', ']')).ToArray();
        var rows = Regex.Matches(sql[(insert.Index + insert.Length)..], @"\((?<values>[^)]+)\)");
        var rules = new List<AlertRule>();
        foreach (Match row in rows)
        {
            var values = row.Groups["values"].Value.Split(',').Select(value => value.Trim()).ToArray();
            Assert.Equal(columns.Length, values.Length);
            var rule = new AlertRule();
            for (var index = 0; index < columns.Length; index++)
            {
                var property = typeof(AlertRule).GetProperty(columns[index]);
                Assert.NotNull(property);
                var value = values[index];
                object parsed = property.PropertyType == typeof(Guid) ? Guid.Parse(value.Trim('\''))
                    : property.PropertyType == typeof(bool) ? value == "1"
                    : property.PropertyType == typeof(int) ? int.Parse(value, CultureInfo.InvariantCulture)
                    : value[(value.StartsWith("N'", StringComparison.Ordinal) ? 2 : 1)..^1].Replace("''", "'");
                property.SetValue(rule, parsed);
            }
            rules.Add(rule);
        }
        Assert.Equal(10, rules.Count);
        Assert.Equal(JsonSerializer.Serialize(DefaultAlertRules.Create().OrderBy(rule => rule.Id)),
            JsonSerializer.Serialize(rules.OrderBy(rule => rule.Id)));

        var tables = Regex.Matches(script, @"\(\d+, N'(?<table>[^']+)'\)")
            .Select(match => match.Groups["table"].Value).ToArray();
        Assert.Equal(
            ["RcaAlertLinks", "AgentAnalysisReviewEvents", "AgentAnalysisJobs", "AnalysisDashboardTemplates",
             "AnalysisDashboards", "InboxLayouts", "AnalysisQueryWorkspaces", "AnalysisQueryVersions",
             "AnalysisQueryDefinitions", "LogbookEntries", "ParsedAlerts", "AlertHistory",
             "Alerts", "ComputerInventory", "AlertRules", "AnalysisQueryCategories",
             "RcaChanges", "RcaPrompts", "RootCauseAnalyses"], tables);
        Assert.Contains("DECLARE @CommitChanges bit = 0;", script);
        Assert.Contains("DECLARE @Confirmation nvarchar(32) = N'';", script);
        Assert.Contains("DB_NAME() <> @ExpectedDatabase", script);
        Assert.Contains("ROLLBACK TRANSACTION;", script);
        Assert.Contains("ERROR_MESSAGE() AS [ErrorMessage]", script);
        Assert.Contains("THROW;", script);
    }

    [Fact]
    public async Task RendersWarningAndResetEntryPointWithoutTouchingDatabase()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var renderer = new HtmlRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<DatabaseResetPanel>()).ToHtmlString());

        Assert.Contains("Initialize / reset database...", html);
        Assert.Contains("Permanent deletion. Back up the database first.", html);
        Assert.Contains("RCA cases with prompts, analysis, comments and changelogs", html);
        Assert.Contains("AD query templates are not installed automatically.", html);
        Assert.Contains("are preserved.", html);
        Assert.DoesNotContain("aria-modal=\"true\"", html);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(0, await context.AlertRules.CountAsync());
    }

    private static ClaimsPrincipal User(string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "reset-admin") };
        if (role is not null)
            claims.Add(new(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "test"));
    }

    private static async Task AssertOperationalCountsAsync(AlertDbContext context, int count)
    {
        Assert.Equal(count, await context.Alerts.CountAsync());
        Assert.Equal(count, await context.AlertHistory.CountAsync());
        Assert.Equal(count, await context.ParsedAlerts.CountAsync());
        Assert.Equal(count, await context.ComputerInventory.CountAsync());
        Assert.Equal(count, await context.LogbookEntries.CountAsync());
        Assert.Equal(count, await context.AgentAnalysisJobs.CountAsync());
        Assert.Equal(count, await context.AgentAnalysisReviewEvents.CountAsync());
        Assert.Equal(count, await context.AnalysisQueryDefinitions.CountAsync());
        Assert.Equal(count, await context.AnalysisQueryCategories.CountAsync());
        Assert.Equal(count, await context.AnalysisQueryWorkspaces.CountAsync());
        Assert.Equal(count, await context.AnalysisQueryVersions.CountAsync());
        Assert.Equal(count, await context.AnalysisDashboards.CountAsync());
        Assert.Equal(count, await context.AnalysisDashboardTemplates.CountAsync());
        Assert.Equal(count, await context.InboxLayouts.CountAsync());
        Assert.Equal(count, await context.RootCauseAnalyses.CountAsync());
        Assert.Equal(count, await context.RcaPrompts.CountAsync());
        Assert.Equal(count, await context.RcaChanges.CountAsync());
        Assert.Equal(count, await context.RcaAlertLinks.CountAsync());
    }

    private static async Task AssertOriginalDataAsync(AlertDbContext context)
    {
        await AssertOperationalCountsAsync(context, 1);
        Assert.Equal("Custom rule", (await context.AlertRules.SingleAsync()).Name);
        Assert.Equal("Keep comment", (await context.Alerts.SingleAsync()).Comments);
        await AssertPreservedAsync(context);
    }

    private static async Task AssertPreservedAsync(AlertDbContext context)
    {
        var user = await context.AuthenticationUsers.SingleAsync();
        Assert.Equal("admin", user.Username);
        Assert.Equal(SqlAuthenticationRoles.Admin, user.Role);
        Assert.Equal("unchanged-password-hash", user.PasswordHash);
        var settings = await context.Settings.ToDictionaryAsync(setting => setting.Name, setting => setting.JsonValue);
        Assert.Equal(2, settings.Count);
        Assert.Equal("{\"Type\":\"sql\"}", settings["Authentication"]);
        Assert.Equal("{\"Workspace\":\"keep\"}", settings["CustomConnection"]);
        Assert.Equal(["test-migration"], await context.Database.GetAppliedMigrationsAsync());
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, TestDbContextFactory factory) : IAsyncDisposable
    {
        public ServiceProvider Services => services;
        public IDbContextFactory<AlertDbContext> Factory => factory;
        public DatabaseResetService Reset => services.GetRequiredService<DatabaseResetService>();
        public AlertStore Store => services.GetRequiredService<AlertStore>();

        public static async Task<Fixture> CreateAsync(bool open = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement
                    .Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            await context.Database.ExecuteSqlRawAsync(
                """CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL PRIMARY KEY, "ProductVersion" TEXT NOT NULL);""");
            await context.Database.ExecuteSqlRawAsync(
                """INSERT INTO "__EFMigrationsHistory" VALUES ('test-migration', '10.0.11');""");

            var services = new ServiceCollection().AddLogging().AddAuthorization(options =>
                options.AddPolicy("AdminAccess", policy => policy.RequireAssertion(context =>
                    open || context.User.IsInRole(SqlAuthenticationRoles.Admin))))
                .AddSingleton<IDbContextFactory<AlertDbContext>>(factory)
                .AddSingleton(new DatabaseConfigurationStatus(true, null,
                    new SqlConnectionStringBuilder { DataSource = "test-server", InitialCatalog = "test-database" }))
                .AddSingleton(new ApplicationAuthenticationOptions
                {
                    Type = open ? ApplicationAuthenticationOptions.Open : ApplicationAuthenticationOptions.Sql
                })
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
                .AddSingleton(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")))
                .AddSingleton<AlertRuleEvaluator>()
                .AddSingleton<AlertStore>()
                .AddSingleton<DatabaseResetService>()
                .AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider())
                .BuildServiceProvider();
            return new(connection, services, factory);
        }

        public async Task SeedAsync()
        {
            await using var context = factory.CreateDbContext();
            var alert = new AlertRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "alert", "Test alert", "Sev1",
                "New", "Log", "Fired", "DC01", "rg", "sub", DateTimeOffset.UtcNow, "", "", "Keep comment", "{}");
            var inventory = new ComputerInventoryEntry { SubscriptionId = "sub", Computer = "DC01" };
            context.Alerts.Add(alert);
            context.AlertHistory.Add(AlertHistoryRecord.Create(alert));
            context.ComputerInventory.Add(inventory);
            context.ParsedAlerts.Add(new()
            {
                Id = alert.Id, Alert = alert, InventoryComputerEntry = inventory,
                InventorySubscriptionId = inventory.SubscriptionId, InventoryComputer = inventory.Computer
            });
            context.LogbookEntries.Add(new() { Id = Guid.NewGuid(), AlertRecordId = alert.Id, User = "admin", Comment = "Keep logbook" });
            context.AlertRules.Add(new() { Id = Guid.NewGuid(), Name = "Custom rule" });
            var job = new AgentAnalysisJob { Id = Guid.NewGuid(), AnalysisId = Guid.NewGuid(), IdempotencyKey = "test" };
            context.AgentAnalysisJobs.Add(job);
            context.AgentAnalysisReviewEvents.Add(new() { Id = Guid.NewGuid(), AgentAnalysisJobId = job.Id, AnalysisId = job.AnalysisId });
            var query = new AnalysisQueryDefinition { Id = Guid.NewGuid(), Key = "custom", Name = "Custom query" };
            query.Workspaces.Add(new() { WorkspaceKey = "test" });
            var version = new AnalysisQueryVersion { Id = Guid.NewGuid(), Version = 1, SnapshotJson = "{}" };
            query.Versions.Add(version);
            query.PublishedVersionId = version.Id;
            context.AnalysisQueryDefinitions.Add(query);
            context.AnalysisQueryCategories.Add(new() { Path = "Custom" });
            context.AnalysisDashboards.Add(new() { OwnerKey = "admin", Name = "Custom dashboard", Revision = 1 });
            context.AnalysisDashboardTemplates.Add(new() { Id = Guid.NewGuid(), Name = "Shared dashboard", Published = true, UpdatedBy = "admin" });
            context.InboxLayouts.Add(new() { OwnerKey = "admin", Name = "Custom inbox" });
            context.AuthenticationUsers.Add(new() { Username = "admin", PasswordHash = "unchanged-password-hash", Role = SqlAuthenticationRoles.Admin });
            var rca = new RootCauseAnalysis { Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CategoryName = "Incident" };
            context.RootCauseAnalyses.Add(rca);
            context.RcaAlertLinks.Add(new() { RcaId = rca.Id, AlertEventId = alert.Id });
            context.RcaPrompts.Add(new() { Id = Guid.NewGuid(), RcaId = rca.Id, RawPrompt = "Evidence" });
            context.RcaChanges.Add(new() { Id = Guid.NewGuid(), RcaId = rca.Id, Actor = "admin", Field = "Comment", After = "Keep comment" });
            context.Settings.AddRange(
                new DatabaseSetting { Name = "Authentication", JsonValue = "{\"Type\":\"sql\"}" },
                new DatabaseSetting { Name = "CustomConnection", JsonValue = "{\"Workspace\":\"keep\"}" });
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(User(SqlAuthenticationRoles.Admin)));
    }
}
