using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace MonitoringApp.Tests;

public sealed class AlertInventoryLearningTests
{
    [Fact]
    public void LearnsLocalAndRemoteDomainsFromDcPortRows()
    {
        const string rawJson = """
            {
              "queryResult": {
                "type": "DCPort",
                "columns": [
                  { "name": "RemoteSystem", "type": "string" },
                  { "name": "RemoteDomain", "type": "string" },
                  { "name": "LocalDomain", "type": "string" },
                  { "name": "LocalSystem", "type": "string" }
                ],
                "rows": [
                  ["REMOTE-DC", "remote.example", "local.example", "LOCAL-DC"]
                ]
              }
            }
            """;

        var learned = AlertInventoryLearning.LearnComputerDomains(rawJson);

        Assert.Contains(new LearnedComputerDomain("LOCAL-DC", "local.example"), learned);
        Assert.Contains(new LearnedComputerDomain("REMOTE-DC", "remote.example"), learned);
    }

    [Fact]
    public void IgnoresIncompleteAndConflictingDomainFacts()
    {
        const string rawJson = """
            {
              "queryResult": {
                "type": "DCPort",
                "columns": [
                  { "name": "LocalSystem" },
                  { "name": "LocalDomain" },
                  { "name": "RemoteSystem" },
                  { "name": "RemoteDomain" }
                ],
                "rows": [
                  ["DC-01", "first.example", "DC-02", ""],
                  ["DC-01", "second.example", "DC-03", "remote.example"]
                ]
              }
            }
            """;

        var learned = AlertInventoryLearning.LearnComputerDomains(rawJson);

        Assert.DoesNotContain(learned, fact => fact.Computer == "DC-01");
        Assert.DoesNotContain(learned, fact => fact.Computer == "DC-02");
        Assert.Contains(new LearnedComputerDomain("DC-03", "remote.example"), learned);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"queryResult\":{\"type\":\"Heartbeat\",\"columns\":[],\"rows\":[]}}")]
    public void IgnoresUnsupportedPayloads(string rawJson)
    {
        Assert.Empty(AlertInventoryLearning.LearnComputerDomains(rawJson));
    }

    [Fact]
    public void EnrichesExistingSystemsAcrossSubscriptionsWithoutChangingTheirIdentity()
    {
        var existing = new ComputerInventoryEntry
        {
            SubscriptionId = "registered-sub", Computer = "REMOTE-DC", Domain = "example.test",
            ResourceGroup = "registered-rg", Role = "Domain Controller", Maintenance = true
        };
        var inventory = new[] { existing };

        var skipped = AlertInventoryLearning.EnrichExistingDcPortSystems(DcPortAlert(), inventory);

        Assert.Single(inventory);
        Assert.Equal("registered-sub", existing.SubscriptionId);
        Assert.Equal("registered-rg", existing.ResourceGroup);
        Assert.Equal("Domain Controller", existing.Role);
        Assert.True(existing.Maintenance);
        Assert.Equal("Remote site", existing.Site);
        Assert.Equal(["LOCAL-DC"], skipped);
    }

    [Fact]
    public void PreservesExistingSitesAndSkipsAmbiguousOrConflictingSystems()
    {
        var first = new ComputerInventoryEntry
        {
            SubscriptionId = "sub-a", Computer = "REMOTE-DC", Domain = "example.test", Site = "Known site"
        };
        var second = new ComputerInventoryEntry
        {
            SubscriptionId = "sub-b", Computer = "REMOTE-DC", Domain = "example.test"
        };
        AlertInventoryLearning.EnrichExistingDcPortSystems(DcPortAlert(), [first]);
        Assert.Equal("Known site", first.Site);

        var skipped = AlertInventoryLearning.EnrichExistingDcPortSystems(DcPortAlert(), [first, second]);
        Assert.Contains("REMOTE-DC", skipped);
        Assert.Null(second.Site);

        second.Domain = "different.test";
        AlertInventoryLearning.EnrichExistingDcPortSystems(DcPortAlert(), [second]);
        Assert.Null(second.Site);
        Assert.Equal("different.test", second.Domain);
    }

    [Fact]
    public void MissingDomainsDoNotAllowCrossSubscriptionMatching()
    {
        var existing = new ComputerInventoryEntry
        {
            SubscriptionId = "registered-sub", Computer = "REMOTE-DC"
        };
        AlertInventoryLearning.EnrichExistingDcPortSystems(DcPortAlert(), [existing]);
        Assert.Null(existing.Domain);
        Assert.Null(existing.Site);

        var alert = DcPortAlert() with { RawJson = DcPortPayload.Replace("example.test", "") };
        existing.Domain = "example.test";
        AlertInventoryLearning.EnrichExistingDcPortSystems(alert, [existing]);
        Assert.Null(existing.Site);

        existing.SubscriptionId = "source-sub";
        AlertInventoryLearning.EnrichExistingDcPortSystems(alert, [existing]);
        Assert.Equal("Remote site", existing.Site);
    }

    [Fact]
    public async Task IngestingDcPortAlertsNeverCreatesInventorySystems()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await using (var context = factory.CreateDbContext())
        {
            var names = new[] { "Alerts", "AlertHistory", "ParsedAlerts", "AlertRules", "ComputerInventory",
                "RootCauseAnalyses", "RcaAlertLinks", "RcaPrompts", "RcaChanges" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "registered-sub", Computer = "REMOTE-DC", Domain = "example.test"
            });
            await context.SaveChangesAsync();
        }
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var store = new AlertStore(factory,
            new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
            presenter, new AlertRuleEvaluator(presenter), NullLogger<AlertStore>.Instance);
        using var payload = JsonDocument.Parse(DcPortPayload);

        await store.AddAsync(payload.RootElement);
        await store.AddAsync(payload.RootElement);

        var entry = Assert.Single(await store.GetComputerInventoryAsync());
        Assert.Equal("registered-sub", entry.SubscriptionId);
        Assert.Equal("Remote site", entry.Site);
        await using var verification = factory.CreateDbContext();
        Assert.Single(await verification.Alerts.ToArrayAsync());
        var parsed = Assert.Single(await verification.ParsedAlerts.ToArrayAsync());
        Assert.Equal("registered-sub", parsed.InventorySubscriptionId);
        parsed.InventorySubscriptionId = null;
        parsed.InventoryComputer = null;
        await verification.SaveChangesAsync();

        verification.ComputerInventory.Remove(await verification.ComputerInventory.SingleAsync());
        await verification.SaveChangesAsync();
        using var unknownPayload = JsonDocument.Parse(DcPortPayload.Replace("dcport-test", "dcport-unknown"));
        await store.AddAsync(unknownPayload.RootElement);
        Assert.Empty(await store.GetComputerInventoryAsync());
        Assert.Null((await verification.ParsedAlerts.AsNoTracking().SingleAsync(item => item.AlertId == "dcport-unknown")).InventorySubscriptionId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HistoricalDcPortAlertsOnlyEnrichExistingSystems(bool backfill, bool hasExistingSystem)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlite(connection).ReplaceService<IModelCustomizer, InventoryLearningTestModelCustomizer>().Options);
        var alert = DcPortAlert();
        await using (var context = factory.CreateDbContext())
        {
            var names = new[] { "Alerts", "AlertHistory", "ParsedAlerts", "AlertRules", "ComputerInventory" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            if (hasExistingSystem)
            {
                context.ComputerInventory.Add(new()
                {
                    SubscriptionId = "registered-sub", Computer = "REMOTE-DC", Domain = "example.test"
                });
            }
            context.Alerts.Add(alert);
            await context.SaveChangesAsync();
        }
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var store = new AlertStore(factory,
            new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
            presenter, new AlertRuleEvaluator(presenter), NullLogger<AlertStore>.Instance);
        if (backfill)
        {
            var records = await store.GetAlertGraphRecordsAsync(DateTimeOffset.UtcNow.AddDays(-1));
            if (hasExistingSystem)
                Assert.Contains(records, record => record.AlertId == alert.AlertId &&
                    record.Target == "REMOTE-DC" && record.SubscriptionId == "registered-sub");
            else
                Assert.Empty(records);
            await using var verification = factory.CreateDbContext();
            var parsed = Assert.Single(await verification.ParsedAlerts.ToArrayAsync());
            Assert.Equal(hasExistingSystem ? "registered-sub" : null, parsed.InventorySubscriptionId);
        }
        else
        {
            var result = await store.PrefillComputerInventoryFromLastSevenDaysAsync();
            Assert.Equal(0, result.SystemsCreated);
            Assert.Equal(hasExistingSystem ? 1 : 0, result.SitesFilled);
            var repeated = await store.PrefillComputerInventoryFromLastSevenDaysAsync();
            Assert.Equal(0, repeated.SystemsCreated);
            Assert.Equal(0, repeated.SitesFilled);
        }
        var inventory = await store.GetComputerInventoryAsync();
        if (hasExistingSystem)
        {
            var entry = Assert.Single(inventory);
            Assert.Equal("registered-sub", entry.SubscriptionId);
            Assert.Equal("Remote site", entry.Site);
        }
        else
        {
            Assert.Empty(inventory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphExcludesDeletedInventorySystemsWithoutDeletingAlertHistory(bool dcPort)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlite(connection).ReplaceService<IModelCustomizer, InventoryLearningTestModelCustomizer>().Options);
        var alert = dcPort ? DcPortAlert() : DcPortAlert() with { RawJson = "{}" };
        await using (var context = factory.CreateDbContext())
        {
            var names = new[] { "Alerts", "AlertHistory", "ParsedAlerts", "AlertRules", "ComputerInventory" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            context.ComputerInventory.AddRange(
                new ComputerInventoryEntry
                {
                    SubscriptionId = alert.SubscriptionId, Computer = alert.TargetName,
                    Domain = "example.test", Site = "Known site", LastHeartbeatAt = DateTimeOffset.UtcNow
                },
                new ComputerInventoryEntry { SubscriptionId = "other-sub", Computer = "OTHER-DC" });
            context.Alerts.Add(alert);
            context.ParsedAlerts.Add(ParsedAlertFactory.Create(alert));
            await context.SaveChangesAsync();
        }
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var store = new AlertStore(factory,
            new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
            presenter, new AlertRuleEvaluator(presenter), NullLogger<AlertStore>.Instance);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var before = await store.GetAlertGraphRecordsAsync(cutoff);
        Assert.Equal(3, before.Count);
        Assert.Contains(before, record => record.AlertId == alert.AlertId && record.Domain == "example.test");

        await using (var context = factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"ComputerInventory\" WHERE \"SubscriptionId\" = {alert.SubscriptionId} AND \"Computer\" = {alert.TargetName}");
        }

        var remaining = Assert.Single(await store.GetAlertGraphRecordsAsync(cutoff));
        Assert.True(remaining.IsInventoryOnly);
        Assert.Equal("OTHER-DC", remaining.Target);
        await using var verification = factory.CreateDbContext();
        Assert.Single(await verification.Alerts.ToArrayAsync());
        var parsed = Assert.Single(await verification.ParsedAlerts.ToArrayAsync());
        Assert.Null(parsed.InventorySubscriptionId);
        Assert.Null(parsed.InventoryComputer);
        Assert.Equal(alert.AlertId, parsed.AlertId);
        await verification.Database.ExecuteSqlRawAsync("DELETE FROM \"ComputerInventory\"");
        Assert.Empty(await store.GetAlertGraphRecordsAsync(cutoff));
        Assert.Single(await verification.Alerts.ToArrayAsync());
        Assert.Single(await verification.ParsedAlerts.ToArrayAsync());
    }

    public sealed class InventoryLearningTestModelCustomizer(ModelCustomizerDependencies dependencies)
        : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            // SQLite requires numeric timestamps for the production prefill and backfill date predicates.
            modelBuilder.Entity<AlertRecord>().Property(alert => alert.ReceivedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }
    }

    private static AlertRecord DcPortAlert() => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "dcport-test", "DC Port", "Sev1", "Fired", "Log", "Fired",
        "REMOTE-DC", "source-rg", "source-sub", DateTimeOffset.UtcNow, "", "", "", DcPortPayload);

    private const string DcPortPayload = """
        {
          "alertId": "dcport-test",
          "alertName": "DC Port",
          "subscriptionId": "source-sub",
          "targetResourceName": "REMOTE-DC",
          "monitorCondition": "Fired",
          "queryResult": {
            "type": "DCPort",
            "columns": [
              { "name": "LocalSystem" }, { "name": "LocalDomain" }, { "name": "LocalSite" },
              { "name": "RemoteSystem" }, { "name": "RemoteDomain" }, { "name": "RemoteSite" }
            ],
            "rows": [["LOCAL-DC", "example.test", "Local site", "REMOTE-DC", "example.test", "Remote site"]]
          }
        }
        """;

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }
}