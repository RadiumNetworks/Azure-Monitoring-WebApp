using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class ReplicationInventoryWarningTests
{
    [Fact]
    public async Task ChecksBothSystemsInEveryRowWithoutChangingInventoryAndDeduplicatesWarnings()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedInventoryAsync();
        using var payload = JsonDocument.Parse(Payload);

        Assert.True((await fixture.Store.AddAsync(payload.RootElement)).Created);
        Assert.False((await fixture.Store.AddAsync(payload.RootElement)).Created);
        using var resolved = JsonDocument.Parse(Payload.Replace("\"Fired\"", "\"Resolved\"", StringComparison.Ordinal));
        await fixture.Store.AddAsync(resolved.RootElement);

        await using var context = fixture.Factory.CreateDbContext();
        var inventory = await context.ComputerInventory.ToArrayAsync();
        Assert.Equal(2, inventory.Length);
        var known = Assert.Single(inventory, entry => entry.Computer == "KNOWN-DC");
        Assert.Null(known.Site);
        Assert.Null(known.ResourceGroup);
        Assert.Null(known.Role);
        Assert.Equal("existing.test", known.Domain);

        var warnings = (await context.Alerts.ToArrayAsync()).Where(IsWarning).ToArray();
        Assert.Equal(3, warnings.Length);
        Assert.Equal(["NEW-DEST", "OTHER-SUB-DC", "UNKNOWN-SOURCE"],
            warnings.Select(warning => warning.TargetName).Order(StringComparer.Ordinal).ToArray());
        Assert.All(warnings, warning =>
        {
            Assert.Equal("Sev2", warning.Severity);
            Assert.Equal("Fired", warning.MonitorCondition);
            Assert.Equal("source-sub", warning.SubscriptionId);
            Assert.Equal("https://example.test/results", warning.SearchResultsUrl);
            Assert.Contains("replication-test", warning.Description, StringComparison.Ordinal);
            Assert.Equal(warning.TargetName, fixture.Presenter.ResolveDisplayIdentity(warning).TargetName);
        });
        var parsed = await context.ParsedAlerts.ToArrayAsync();
        Assert.Equal(5, parsed.Length);
        Assert.All(parsed.Where(item => warnings.Any(warning => warning.Id == item.Id)), item =>
        {
            Assert.Null(item.InventorySubscriptionId);
            Assert.Null(item.InventoryComputer);
        });
        Assert.Equal(4, await context.AlertHistory.CountAsync());
        var graph = await fixture.Store.GetAlertGraphRecordsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        Assert.DoesNotContain(graph, item => item.Target is "NEW-DEST" or "UNKNOWN-SOURCE");
    }

    [Fact]
    public async Task EmptyInventoryDoesNotAcquireReplicationTargetsOrWarningTargets()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var payload = JsonDocument.Parse(Payload);
        await fixture.Store.AddAsync(payload.RootElement);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.ComputerInventory.ToArrayAsync());
        var warnings = (await context.Alerts.ToArrayAsync()).Where(IsWarning).ToArray();
        Assert.Equal(4, warnings.Length);
        foreach (var warning in warnings)
        {
            using var warningPayload = JsonDocument.Parse(warning.RawJson);
            await fixture.Store.AddAsync(warningPayload.RootElement);
        }
        Assert.Empty(await fixture.Store.GetComputerInventoryAsync());
        Assert.Empty(await fixture.Store.GetAlertGraphRecordsAsync(DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task DisabledDeletedAndReenabledRuleControlsWarningsIncludingDuplicateDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = Assert.Single(await fixture.Store.GetAllAlertRulesAsync(),
            candidate => candidate.RuleType == AlertRuleTypes.ReplicationInventoryWarning);
        rule.Enabled = false;
        rule = await fixture.Store.SaveAlertRuleAsync(rule);
        using var payload = JsonDocument.Parse(Payload);
        await fixture.Store.AddAsync(payload.RootElement);
        await using (var context = fixture.Factory.CreateDbContext())
            Assert.DoesNotContain(await context.Alerts.ToArrayAsync(), IsWarning);
        Assert.Empty(await fixture.Store.GetComputerInventoryAsync());

        rule.Enabled = true;
        await fixture.Store.SaveAlertRuleAsync(rule);
        var notifications = 0;
        fixture.Store.Changed += () => notifications++;
        Assert.False((await fixture.Store.AddAsync(payload.RootElement)).Created);
        Assert.Equal(1, notifications);
        Assert.False((await fixture.Store.AddAsync(payload.RootElement)).Created);
        Assert.Equal(1, notifications);

        Assert.True(await fixture.Store.DeleteAlertRuleAsync(rule.Id));
        using var next = JsonDocument.Parse(Payload.Replace("replication-test", "replication-next", StringComparison.Ordinal));
        await fixture.Store.AddAsync(next.RootElement);
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(4, (await check.Alerts.ToArrayAsync()).Count(IsWarning));
        Assert.Empty(await fixture.Store.GetComputerInventoryAsync());
    }

    [Fact]
    public async Task HistoricalParsedBackfillDoesNotLearnReplicationOrWarningData()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedInventoryAsync();
        using var payload = JsonDocument.Parse(Payload);
        var source = AlertPayloadForTest(payload.RootElement);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.Alerts.Add(source);
            context.Alerts.Add(ReplicationInventoryWarning.Create(source, WarningRule(), "NEW-DEST"));
            await context.SaveChangesAsync();
        }
        await fixture.Store.GetAlertGraphRecordsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var entries = await fixture.Store.GetComputerInventoryAsync();
        Assert.Equal(2, entries.Count);
        var known = Assert.Single(entries, entry => entry.Computer == "KNOWN-DC");
        Assert.Null(known.Site);
        Assert.Null(known.ResourceGroup);
        Assert.Null(known.Role);
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await check.Alerts.CountAsync());
        var warning = Assert.Single(await check.ParsedAlerts.ToArrayAsync(),
            item => item.AlertName == ReplicationInventoryWarning.AlertName);
        Assert.Null(warning.InventorySubscriptionId);
        Assert.Null(warning.InventoryComputer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData("0")]
    public async Task ZeroRowCountDoesNotLearnOrWarn(object rowCount)
    {
        await using var fixture = await Fixture.CreateAsync();
        var json = Payload.Replace("\"rowCount\": 4", $"\"rowCount\": {JsonSerializer.Serialize(rowCount)}", StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(json);
        await fixture.Store.AddAsync(payload.RootElement);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Single(await context.Alerts.ToArrayAsync());
        Assert.Empty(await context.ComputerInventory.ToArrayAsync());
    }

    [Fact]
    public async Task SuppressedReplicationStillProducesInventoryWarningsButNeverLearns()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAlertRuleAsync(new()
        {
            Name = "Suppress replication", RuleType = AlertRuleTypes.Suppression,
            QueryResultType = "Replication", ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
        });
        using var payload = JsonDocument.Parse(Payload);
        Assert.True((await fixture.Store.AddAsync(payload.RootElement)).Suppressed);
        Assert.True((await fixture.Store.AddAsync(payload.RootElement)).Suppressed);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(4, await context.Alerts.CountAsync());
        Assert.All(await context.Alerts.ToArrayAsync(), alert => Assert.True(IsWarning(alert)));
        Assert.Empty(await fixture.Store.GetComputerInventoryAsync());
    }

    [Fact]
    public async Task RuleIsSaveableNormalizesItsFixedBehaviorAndRejectsAnotherGlobalRule()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = WarningRule();
        rule.QueryResultType = "DCPort";
        rule.InventoryRole = "ignored";
        rule.IsCritical = true;
        rule.Priority = 12;
        var saved = await fixture.Store.SaveAlertRuleAsync(rule);
        Assert.Equal("Replication", saved.QueryResultType);
        Assert.Equal(0, saved.Priority);
        Assert.Empty(saved.InventoryRole);
        Assert.Empty(saved.ConditionType);
        Assert.False(saved.IsCritical);
        var duplicate = WarningRule();
        duplicate.Id = Guid.NewGuid();
        duplicate.Name = "Another warning rule";
        var exception = await Assert.ThrowsAsync<AlertRuleException>(() => fixture.Store.SaveAlertRuleAsync(duplicate));
        Assert.Equal(AlertRuleErrorCode.DuplicateReplicationWarning, exception.Code);
        Assert.Contains("Only one global replication", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarningAlertsParticipateInCriticalCategorizationAndLogbookWithoutDuplicateEntries()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAlertRuleAsync(new()
        {
            Name = "Critical inventory warnings", QueryResultType = ReplicationInventoryWarning.QueryResultType,
            ConditionType = AlertRuleConditionTypes.RowCountGreaterThan, Threshold = 0,
            CategoryName = "Inventory registration required", IsCritical = true
        });
        using var payload = JsonDocument.Parse(Payload);
        await fixture.Store.AddAsync(payload.RootElement);
        await fixture.Store.AddAsync(payload.RootElement);
        await using var context = fixture.Factory.CreateDbContext();
        var warnings = (await context.ParsedAlerts.ToArrayAsync())
            .Where(item => item.AlertName == ReplicationInventoryWarning.AlertName).ToArray();
        Assert.Equal(4, warnings.Length);
        Assert.All(warnings, warning => Assert.True(warning.IsCritical));
        var logbook = await context.LogbookEntries.ToArrayAsync();
        Assert.Equal(4, logbook.Length);
        Assert.All(logbook, entry => Assert.Contains("Unknown replication inventory target", entry.Comment, StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidAndRepeatedRowValuesAreNotInventedAsTargets()
    {
        var source = new AlertRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "test", "Replication", "Sev1",
            "New", "Log", "Fired", "not-a-row-target", "", "sub", null, "", "", "",
            """
            {"queryResult":{"type":"Replication","columns":[{"name":"sourcedsa"},{"name":"destdsa"}],
            "rows":[["DC-A","dc-a"],["  ","<EMPTY_VALUE>"],[42,null],[],["/path/DC-B","  DC-C  "]]}}
            """);
        Assert.Equal(["DC-A", "DC-B", "DC-C"], ReplicationInventoryWarning.ReadSystems(source));
    }

    private static bool IsWarning(AlertRecord alert) =>
        alert.AlertId.StartsWith("inventory-unknown:", StringComparison.Ordinal);

    private static AlertRule WarningRule() => DefaultAlertRules.Create()
        .Single(rule => rule.RuleType == AlertRuleTypes.ReplicationInventoryWarning);

    private static AlertRecord AlertPayloadForTest(JsonElement payload) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "replication-test", "Replication",
        "Sev1", "New", "Log", "Fired", "KNOWN-DC", "reported-rg", "source-sub",
        DateTimeOffset.UtcNow, "", "", "", payload.GetRawText());

    private const string Payload = """
        {
          "data": {
            "essentials": {
              "alertId": "replication-test", "alertRule": "Replication", "severity": "Sev1",
              "monitorCondition": "Fired", "targetResourceName": "KNOWN-DC",
              "targetSubscriptionId": "source-sub", "targetResourceGroup": "reported-rg"
            },
            "alertContext": {"linkToSearchResultsUI": "https://example.test/results"}
          },
          "queryResult": {
            "type": "Replication", "rowCount": 4,
            "columns": [{"name":"SourceDSA"},{"name":"DestDSA"},{"name":"SourceDSASite"}],
            "rows": [
              ["KNOWN-DC","NEW-DEST","Reported site"],
              ["UNKNOWN-SOURCE","new-dest","Reported site"],
              ["known-dc","<EMPTY_VALUE>","Reported site"],
              ["OTHER-SUB-DC","KNOWN-DC","Reported site"]
            ]
          }
        }
        """;

    private sealed class Fixture(SqliteConnection connection, IDbContextFactory<AlertDbContext> factory) : IAsyncDisposable
    {
        public IDbContextFactory<AlertDbContext> Factory { get; } = factory;
        public QueryResultPresenter Presenter { get; } = new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        public AlertStore Store { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlite(connection)
                .ReplaceService<IModelCustomizer, AlertInventoryLearningTests.InventoryLearningTestModelCustomizer>().Options);
            var fixture = new Fixture(connection, factory);
            await using var context = factory.CreateDbContext();
            var names = new[] { "Alerts", "AlertHistory", "ParsedAlerts", "AlertRules", "ComputerInventory",
                "RootCauseAnalyses", "RcaAlertLinks", "RcaPrompts", "RcaChanges", "LogbookEntries" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            context.AlertRules.AddRange(WarningRule(), new AlertRule
            {
                Id = Guid.NewGuid(), Name = "Legacy replication role",
                RuleType = AlertRuleTypes.InventoryRoleAssignment, QueryResultType = "Replication", InventoryRole = "Domain Controller"
            });
            await context.SaveChangesAsync();
            fixture.Store = new AlertStore(factory,
                new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
                fixture.Presenter, new AlertRuleEvaluator(fixture.Presenter), NullLogger<AlertStore>.Instance);
            return fixture;
        }

        public async Task SeedInventoryAsync()
        {
            await using var context = Factory.CreateDbContext();
            context.ComputerInventory.AddRange(
                new ComputerInventoryEntry { SubscriptionId = "source-sub", Computer = "KNOWN-DC", Domain = "existing.test" },
                new ComputerInventoryEntry { SubscriptionId = "other-sub", Computer = "OTHER-SUB-DC" });
            await context.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }
}
