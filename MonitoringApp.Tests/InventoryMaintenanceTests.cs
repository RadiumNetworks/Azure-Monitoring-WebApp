using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class InventoryMaintenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HidesActiveAndHistoricalAlertsOnlyForMatchingInventorySystem()
    {
        var entry = new ComputerInventoryEntry { SubscriptionId = "sub-a", Computer = "SERVER01", Maintenance = true };
        var fired = Alert("SUB-A", "server01");
        var resolved = fired with { Id = Guid.NewGuid(), MonitorCondition = "Resolved" };
        var otherSubscription = Alert("sub-b", "SERVER01");
        var otherTarget = Alert("sub-a", "SERVER02");
        var alerts = new[] { fired, resolved, otherSubscription, otherTarget };

        Assert.Equal([otherSubscription, otherTarget], InventoryMaintenance.FilterAlerts(alerts, [entry]));
        Assert.True(entry.Maintenance);
        Assert.Equal("Fired", fired.MonitorCondition);
        entry.Maintenance = false;
        Assert.Equal(alerts, InventoryMaintenance.FilterAlerts(alerts, [entry]));
    }

    [Fact]
    public void GraphPropagatesMaintenanceThroughHierarchyAndLayout()
    {
        var record = new AlertGraphRecord(Guid.NewGuid(), Now, "alert", "Fired", "", "Test",
            "sub-a", "rg-a", "SERVER01", "Site A", "example.test", "DC", Maintenance: true);
        var inventory = record with { Id = Guid.NewGuid(), AlertId = "", MonitorCondition = "", IsInventoryOnly = true };
        var hierarchy = AlertGraphHierarchy.Build([record, inventory],
            AlertGraphLayer.Subscription, AlertGraphLayer.Site, AlertGraphLayer.Target);
        var leaf = Assert.Single(Assert.Single(Assert.Single(hierarchy).Children).Children);
        Assert.True(leaf.Maintenance);
        Assert.Equal(1, leaf.Count);
        var node = Assert.Single(AlertGraphLayout.Build(hierarchy).Nodes, node => node.Layer == AlertGraphLayer.Target);
        Assert.True(node.Maintenance);
        Assert.Equal(AlertGraphTargetStatus.Maintenance, AlertGraphTargetStatusResolver.Resolve(node, Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameSystemAcrossSubscriptionsCombinesMaintenance(bool secondMaintenance)
    {
        AlertGraphHierarchyNode Root(string subscription, bool maintenance) =>
            new(subscription, AlertGraphLayer.Subscription, 1, 0, 0,
                [new("site", AlertGraphLayer.Site, 2, 0, 0,
                    [new("SERVER01", AlertGraphLayer.Target, 3, 0, 0, [], Maintenance: maintenance)])]);
        var targets = AlertGraphLayout.Build([Root("sub-a", true), Root("sub-b", secondMaintenance)]).Nodes
            .Where(node => node.Layer == AlertGraphLayer.Target).ToArray();
        Assert.Equal(secondMaintenance, Assert.Single(targets).Maintenance);
    }

    [Fact]
    public async Task SavesMaintenanceWithoutChangingStoredAlertsOrHeartbeatAndRestoresVisibility()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        var alert = Alert("sub-a", "SERVER01");
        await using (var context = factory.CreateDbContext())
        {
            var names = new[] { "Alerts", "AlertHistory", "ParsedAlerts", "AlertRules", "ComputerInventory" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
            {
                await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            }
            context.Alerts.Add(alert);
            await context.SaveChangesAsync();
        }
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var store = new AlertStore(factory,
            new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
            presenter, new AlertRuleEvaluator(presenter), NullLogger<AlertStore>.Instance);
        var notifications = 0;
        store.Changed += () => notifications++;

        var entry = await store.CreateComputerInventoryEntryAsync(
            new ComputerInventoryEntry { SubscriptionId = "sub-a", Computer = "SERVER01", Maintenance = true });
        Assert.True(Assert.Single(await store.GetComputerInventoryAsync()).Maintenance);
        Assert.Empty(InventoryMaintenance.FilterAlerts([alert], await store.GetComputerInventoryAsync()));

        await using (var context = factory.CreateDbContext())
        {
            var storedEntry = await context.ComputerInventory.SingleAsync();
            storedEntry.LastHeartbeatAt = Now;
            var parsed = ParsedAlertFactory.Create(alert);
            parsed.InventorySubscriptionId = "sub-a";
            parsed.InventoryComputer = "SERVER01";
            context.ParsedAlerts.Add(parsed);
            context.AlertRules.Add(new AlertRule
            {
                Id = Guid.NewGuid(),
                Name = "Inventory heartbeat",
                RuleType = AlertRuleTypes.InventoryStatus,
                QueryResultType = "InventoryHeartbeat",
                ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
            });
            await context.SaveChangesAsync();
        }

        await store.SaveComputerInventoryEntryAsync(entry);
        Assert.Equal(Now, Assert.Single(await store.GetComputerInventoryAsync()).LastHeartbeatAt);

        using var incoming = System.Text.Json.JsonDocument.Parse("""
            {
              "alertId": "maintenance-arrival",
              "alertName": "CPU alert",
              "monitorCondition": "Fired",
              "targetSubscriptionId": "sub-a",
              "targetResourceName": "SERVER01"
            }
            """);
        Assert.True((await store.AddAsync(incoming.RootElement)).Created);
        Assert.True(Assert.Single(await store.GetComputerInventoryAsync()).Maintenance);
        await using (var context = factory.CreateDbContext())
        {
            var firedHistory = Assert.Single(await context.AlertHistory.AsNoTracking().ToArrayAsync());
            Assert.Equal("maintenance-arrival", firedHistory.AlertId);
            Assert.Equal("Fired", firedHistory.MonitorCondition);
        }

        using var heartbeat = System.Text.Json.JsonDocument.Parse("""
            {
              "alertId": "maintenance-heartbeat",
              "alertName": "Inventory Heartbeat Status",
              "queryResult": {
                "type": "InventoryHeartbeat",
                "columns": [
                  { "name": "SubscriptionId" },
                  { "name": "Computer" },
                  { "name": "LastHeartbeat" },
                  { "name": "NTDSFileSizeinGB" },
                  { "name": "Certificates" },
                  { "name": "Services" },
                  { "name": "TenantId" },
                  { "name": "Site" }
                ],
                "rows": [[
                  "sub-a",
                  "SERVER01",
                  "2026-10-09T10:01:00Z",
                  8.25,
                  "{\"Subject\":\"CN=SERVER01\",\"Thumbprint\":\"ABC\",\"NotAfter\":\"2027-10-09T10:01:00Z\"}",
                  "[{\"Name\":\"NTDS\",\"Status\":4}]",
                  "tenant-a",
                  "Frankfurt"
                ]]
              }
            }
            """);
        await store.AddAsync(heartbeat.RootElement);
        var observed = Assert.Single(await store.GetComputerInventoryAsync());
        Assert.True(observed.Maintenance);
        Assert.Equal(Now.AddMinutes(1), observed.LastHeartbeatAt);
        Assert.Equal(8.25, observed.NtdsFileSizeInGb);
        Assert.Equal("Frankfurt", observed.Site);
        Assert.Equal("Domain Controller", observed.Role);
        Assert.Equal("ABC", Assert.Single(observed.Certificates).SerialNumber);
        Assert.Equal(new InventoryService("NTDS", 4), Assert.Single(observed.Services));

        await using (var context = factory.CreateDbContext())
        {
            Assert.All(await context.Alerts.ToArrayAsync(), item => Assert.Equal("Fired", item.MonitorCondition));
            Assert.Equal(2, await context.Alerts.CountAsync());
            Assert.Equal(2, await context.ParsedAlerts.CountAsync());
        }

        entry.Maintenance = false;
        await store.SaveComputerInventoryEntryAsync(entry);
        Assert.False(Assert.Single(await store.GetComputerInventoryAsync()).Maintenance);
        Assert.Single(InventoryMaintenance.FilterAlerts([alert], await store.GetComputerInventoryAsync()));
        Assert.Equal(5, notifications);
    }

    private static AlertRecord Alert(string subscription, string target) => new(
        Guid.NewGuid(), Now, Guid.NewGuid().ToString(), "Test", "Sev1", "Fired", "Log", "Fired",
        target, "rg-a", subscription, Now, "", "", "", "{}")
    {
        DisplayIdentity = new(target, "Site A")
    };

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }
}
