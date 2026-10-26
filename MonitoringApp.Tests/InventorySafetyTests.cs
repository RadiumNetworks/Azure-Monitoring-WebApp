using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class InventorySafetyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData("""{"queryResult":{"rowCount":0}}""", true)]
    [InlineData("""{"queryResult":{"rowCount":0.0}}""", true)]
    [InlineData("""{"queryResult":{"rowCount":"0"}}""", true)]
    [InlineData("""{"queryResult":{"rowCount":1}}""", false)]
    [InlineData("""{"queryResult":{"rowCount":null}}""", false)]
    [InlineData("""{"queryResult":{"rows":[]}}""", false)]
    [InlineData("{}", false)]
    public void RecognizesExplicitZeroRowCountsWithoutChangingMissingCountBehavior(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AlertInventoryLearning.HasZeroRowCount(document.RootElement));
        Assert.Equal(expected, AlertInventoryLearning.HasZeroRowCount(json));
    }

    [Theory]
    [InlineData("DCDiag", false)]
    [InlineData("DCDiag", true)]
    [InlineData("DCPort", false)]
    [InlineData("DCPort", true)]
    [InlineData("InventoryHeartbeat", false)]
    [InlineData("InventoryHeartbeat", true)]
    public async Task ZeroRowIngestionNeverCreatesOrModifiesInventory(string type, bool hasInventory)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (hasInventory)
            await fixture.AddInventoryAsync();
        using var payload = JsonDocument.Parse(Payload(type));
        await fixture.Store.AddAsync(payload.RootElement);
        await fixture.Store.AddAsync(payload.RootElement);
        await using var context = fixture.Factory.CreateDbContext();
        var entries = await context.ComputerInventory.ToArrayAsync();
        if (hasInventory)
        {
            var entry = Assert.Single(entries);
            Assert.Equal("known.example", entry.Domain);
            Assert.Null(entry.Site);
            Assert.Null(entry.Role);
            Assert.Null(entry.ResourceGroup);
            Assert.Null(entry.PrivateIP);
            Assert.Null(entry.LastHeartbeatAt);
        }
        else
            Assert.Empty(entries);
        if (type == "InventoryHeartbeat")
            Assert.Empty(await context.Alerts.ToArrayAsync());
        else
        {
            Assert.Single(await context.Alerts.ToArrayAsync());
            var parsed = Assert.Single(await context.ParsedAlerts.ToArrayAsync());
            Assert.Equal(hasInventory ? "sub" : null, parsed.InventorySubscriptionId);
            Assert.Equal(hasInventory ? "DC01" : null, parsed.InventoryComputer);
        }
        Assert.Empty(AlertInventoryLearning.LearnComputerDomains(Payload("DCPort")));
        using var heartbeat = JsonDocument.Parse(Payload("InventoryHeartbeat"));
        Assert.Empty(InventoryHeartbeatBatch.Parse(heartbeat.RootElement));
    }

    [Theory]
    [InlineData(false, "AzureADConnectHealthAgent", "Entra Connect")]
    [InlineData(true, "AzureADConnectHealthAgent", "Entra Connect")]
    [InlineData(true, "OtherService", "Custom role")]
    public async Task InventoryHeartbeatPersistsDetectedEntraConnectRole(
        bool hasInventory, string serviceName, string expectedRole)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (hasInventory)
        {
            await fixture.AddInventoryAsync();
            await using var existingContext = fixture.Factory.CreateDbContext();
            (await existingContext.ComputerInventory.SingleAsync()).Role = "Custom role";
            await existingContext.SaveChangesAsync();
        }
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            alertId = "entra-connect-inventory",
            alertRule = InventoryHeartbeatBatch.AlertName,
            monitorCondition = "Fired",
            queryResult = new
            {
                type = InventoryHeartbeatBatch.QueryResultType,
                rowCount = 1,
                columns = new[]
                {
                    new { name = "SubscriptionId" }, new { name = "Computer" },
                    new { name = "LastHeartbeat" }, new { name = "Services" }
                },
                rows = new[] { new object[] { "sub", "DC01", Now.ToString("O"),
                    new[] { new InventoryService(serviceName, 1) } } }
            }
        }));

        await fixture.Store.AddAsync(payload.RootElement);
        await using var context = fixture.Factory.CreateDbContext();
        var inventory = Assert.Single(await context.ComputerInventory.ToArrayAsync());
        Assert.Equal(expectedRole, inventory.Role);
        Assert.Equal(serviceName, Assert.Single(inventory.Services).Name);
        Assert.Equal(Now, inventory.LastHeartbeatAt);
        Assert.Empty(await context.Alerts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ZeroRowHistoryCannotLearnInventoryThroughPrefillOrBackfill(
        bool backfill, bool dcPort, bool hasInventory)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (hasInventory)
            await fixture.AddInventoryAsync();
        await fixture.AddAlertAsync("zero-history", rawJson: Payload(dcPort ? "DCPort" : "DCDiag"));
        if (backfill)
            await fixture.Store.GetAlertGraphRecordsAsync(Now.AddDays(-1));
        else
        {
            var result = await fixture.Store.PrefillComputerInventoryFromLastSevenDaysAsync();
            Assert.Equal(0, result.SystemsCreated);
            Assert.Equal(0, result.SitesFilled);
            Assert.Equal(0, result.DomainsFilled);
            Assert.Equal(0, result.ResourceGroupsFilled);
            Assert.Equal(0, result.RolesAssigned);
        }
        await using var context = fixture.Factory.CreateDbContext();
        var entries = await context.ComputerInventory.ToArrayAsync();
        Assert.Equal(hasInventory ? 1 : 0, entries.Length);
        if (hasInventory)
        {
            Assert.Equal("known.example", entries[0].Domain);
            Assert.Null(entries[0].Site);
            Assert.Null(entries[0].Role);
            Assert.Null(entries[0].ResourceGroup);
        }
        if (backfill)
        {
            var parsed = Assert.Single(await context.ParsedAlerts.ToArrayAsync());
            Assert.Equal(hasInventory ? "sub" : null, parsed.InventorySubscriptionId);
        }
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Operator")]
    [InlineData(null)]
    public async Task LegacyResolutionEnforcesAdminForPreviewAndExecution(string? role)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("legacy");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.CountAsync(User(role)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ResolveAsync(User(role), "RESOLVE"));
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.LogbookEntries.ToArrayAsync());
        Assert.Equal("", (await context.Alerts.SingleAsync()).Comments);
    }

    [Fact]
    public async Task LegacyResolutionRequiresExactConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("legacy");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ResolveAsync(User(), "resolve"));
        Assert.Equal(1, await fixture.Service.CountAsync(User()));
    }

    [Fact]
    public async Task ResolvesOnlyActiveUnassignedAlertsAndPreservesHistoryWithAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacyId = await fixture.AddAlertAsync("legacy", comments: "Original comment");
        var parsedId = await fixture.AddAlertAsync("parsed", parsed: true);
        await fixture.AddAlertAsync("", parsed: true, computer: "NO-ID");
        await fixture.AddInventoryAsync();
        var linkedId = await fixture.AddAlertAsync("linked", parsed: true, assigned: true);
        await fixture.AddAlertAsync("resolved", condition: "Resolved");
        await fixture.AddAlertAsync("manual", comments: "Resolved");
        await fixture.AddAlertAsync("completed");
        await fixture.AddAlertAsync("completed", condition: "Resolved", at: Now.AddMinutes(-1));
        Assert.Equal(3, await fixture.Service.CountAsync(User()));
        var notifications = 0;
        fixture.Store.Changed += () => notifications++;
        Assert.Equal(3, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(1, notifications);
        Assert.Equal(0, await fixture.Service.CountAsync(User()));
        await using var context = fixture.Factory.CreateDbContext();
        var alerts = await context.Alerts.ToArrayAsync();
        Assert.Equal(8, alerts.Length);
        Assert.Equal(linkedId, Assert.Single(AlertLifecycle.GetActiveAlerts(alerts)).Id);
        Assert.Equal("Original comment\nResolved", alerts.Single(alert => alert.Id == legacyId).Comments);
        Assert.Equal("Fired", alerts.Single(alert => alert.Id == legacyId).MonitorCondition);
        var audit = await context.LogbookEntries.ToArrayAsync();
        Assert.Equal(3, audit.Length);
        Assert.All(audit, entry =>
        {
            Assert.Equal("admin@example.org", entry.User);
            Assert.Equal(Now, entry.CreatedAt);
            Assert.NotNull(entry.AlertRecordId);
        });
        var parsed = await context.ParsedAlerts.Include(record => record.Alert).ToArrayAsync();
        Assert.All(parsed.Where(record => record.Id == legacyId || record.Id == parsedId),
            record => Assert.Equal(Now, record.ResolvedAt));
        ParsedAlertLifecycle.Synchronize(parsed, await context.AlertRules.ToArrayAsync(),
            fixture.Services.GetRequiredService<AlertRuleEvaluator>());
        Assert.All(parsed.Where(record => record.Id == legacyId || record.Id == parsedId),
            record => Assert.Equal(Now, record.ResolvedAt));
        Assert.Single(await context.ComputerInventory.ToArrayAsync());
        Assert.Equal(0, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task LegacyResolutionRechecksNewInventoryAssignmentsAfterPreview()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddAlertAsync("legacy", parsed: true);
        Assert.Equal(1, await fixture.Service.CountAsync(User()));
        await fixture.AddInventoryAsync();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var parsed = await context.ParsedAlerts.SingleAsync(record => record.Id == id);
            parsed.InventorySubscriptionId = "sub";
            parsed.InventoryComputer = "DC01";
            await context.SaveChangesAsync();
        }
        Assert.Equal(0, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal("", (await check.Alerts.SingleAsync()).Comments);
        Assert.Empty(await check.LogbookEntries.ToArrayAsync());
        Assert.Empty(await check.RootCauseAnalyses.ToArrayAsync());
    }

    [Fact]
    public async Task LegacyResolutionEndsCriticalActivityWithoutClosingOrDeletingRca()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("legacy", parsed: true);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await RcaAutomation.ReconcileAsync(context,
                fixture.Services.GetRequiredService<AlertRuleEvaluator>(),
                fixture.Services.GetRequiredService<QueryResultPresenter>(), Now.AddMinutes(-1), default);
            await context.SaveChangesAsync();
        }
        Assert.Equal(1, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        await using var check = fixture.Factory.CreateDbContext();
        var rca = await check.RootCauseAnalyses.SingleAsync();
        Assert.False(rca.IsActive);
        Assert.Equal("Open", rca.Status);
        Assert.Equal(Now, rca.CriticalEndedAt);
        Assert.Single(await check.RcaPrompts.ToArrayAsync());
        Assert.Single(await check.Alerts.ToArrayAsync());
        Assert.Single(await check.RcaAlertLinks.ToArrayAsync());
    }

    [Fact]
    public async Task AuditFailureRollsBackAllLegacyResolutions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("one");
        await fixture.AddAlertAsync("two");
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailAudit BEFORE INSERT ON LogbookEntries BEGIN SELECT RAISE(ABORT, 'Audit failed'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(2, await fixture.Service.CountAsync(User()));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.All(await check.Alerts.ToArrayAsync(), alert => Assert.Equal("", alert.Comments));
        Assert.Empty(await check.LogbookEntries.ToArrayAsync());
    }

    [Fact]
    public async Task FullCommentsAreNotTruncatedAndPreventPartialResolution()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("one");
        await fixture.AddAlertAsync("two", comments: new string('x', 4000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(2, await fixture.Service.CountAsync(User()));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Empty(await check.LogbookEntries.ToArrayAsync());
    }

    [Fact]
    public async Task RendersSeparateLegacyActionWithClearInventoryScope()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var renderer = new HtmlRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<LegacyAlertResolutionPanel>()).ToHtmlString());
        Assert.Contains("Resolve legacy alerts", html);
        Assert.Contains("Duplicate inventory entries", html);
        Assert.DoesNotContain("Permanently reset database", html);
    }

    private static ClaimsPrincipal User(string? role = "Admin") => new(new ClaimsIdentity(
        role is null ? [] : new[] { new Claim(ClaimTypes.Name, "admin@example.org"), new Claim(ClaimTypes.Role, role) },
        role is null ? null : "test"));

    private static string Payload(string type) => JsonSerializer.Serialize(new
    {
        alertId = $"zero-{type}", alertRule = "Inventory Heartbeat Status", monitorCondition = "Fired",
        targetResourceName = "DC01", targetSubscriptionId = "sub", targetResourceGroup = "new-rg",
        queryResult = new
        {
            type, rowCount = 0,
            columns = new[]
            {
                new { name = "SubscriptionId" }, new { name = "Computer" }, new { name = "Domain" },
                new { name = "Site" }, new { name = "LastHeartbeat" }, new { name = "PrivateIP" },
                new { name = "LocalSystem" }, new { name = "LocalDomain" }, new { name = "LocalSite" },
                new { name = "Services" }
            },
            rows = new[] { new[] { "sub", "DC01", "changed.example", "New site", Now.ToString("O"),
                "10.0.0.1", "DC01", "changed.example", "New site",
                JsonSerializer.Serialize(new[] { new InventoryService("AzureADConnectHealthAgent", 4) }) } }
        }
    });

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, Factory factory) : IAsyncDisposable
    {
        public Factory Factory => factory;
        public ServiceProvider Services => services;
        public AlertStore Store => services.GetRequiredService<AlertStore>();
        public LegacyAlertResolutionService Service => services.GetRequiredService<LegacyAlertResolutionService>();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, AlertInventoryLearningTests.InventoryLearningTestModelCustomizer>().Options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            context.AlertRules.AddRange(DefaultAlertRules.Create().Where(rule =>
                rule.RuleType is AlertRuleTypes.InventoryStatus or AlertRuleTypes.InventoryRoleAssignment));
            context.AlertRules.Add(new()
            {
                Id = Guid.NewGuid(), Name = "Critical", CategoryName = "Outage", IsCritical = true,
                ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
            });
            await context.SaveChangesAsync();
            var services = new ServiceCollection().AddLogging().AddAuthorization(options =>
                    options.AddPolicy("AdminAccess", policy => policy.RequireRole("Admin")))
                .AddSingleton<IDbContextFactory<AlertDbContext>>(factory)
                .AddSingleton(new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()))
                .AddSingleton(new ApplicationAuthenticationOptions())
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
                .AddSingleton<TimeProvider>(new FixedTime())
                .AddSingleton(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")))
                .AddSingleton<AlertRuleEvaluator>().AddSingleton<AlertStore>()
                .AddSingleton<AuthenticationStateProvider, AdminState>()
                .AddSingleton<LegacyAlertResolutionService>().BuildServiceProvider();
            return new(connection, services, factory);
        }

        public async Task AddInventoryAsync()
        {
            await using var context = factory.CreateDbContext();
            context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "DC01", Domain = "known.example" });
            await context.SaveChangesAsync();
        }

        public async Task<Guid> AddAlertAsync(
            string id, bool parsed = false, bool assigned = false, string condition = "Fired",
            string comments = "", string computer = "DC01", string rawJson = "{}", DateTimeOffset? at = null)
        {
            await using var context = factory.CreateDbContext();
            var alert = new AlertRecord(Guid.NewGuid(), at ?? Now.AddMinutes(-10), id, "Test", "Sev0",
                "New", "Log", condition, computer, "rg", "sub", at ?? Now.AddMinutes(-10), "", "", comments, rawJson);
            context.Alerts.Add(alert);
            if (parsed)
            {
                var record = ParsedAlertFactory.Create(alert);
                if (!assigned)
                {
                    record.InventorySubscriptionId = null;
                    record.InventoryComputer = null;
                }
                context.ParsedAlerts.Add(record);
            }
            await context.SaveChangesAsync();
            return alert.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdminState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(User()));
    }
}
