using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class InventoryHeartbeatFreshnessRuleTests
{
    [Fact]
    public void MissingOrDisabledRuleUsesFiveMinuteDefault()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), InventoryHeartbeatFreshnessRule.Resolve([]));
        Assert.Equal(TimeSpan.FromMinutes(5),
            InventoryHeartbeatFreshnessRule.Resolve([CreateRule(20, enabled: false)]));
        Assert.Equal(TimeSpan.FromMinutes(5), new IncidentAnalysisSnapshotOptions().EffectiveCurrentHeartbeatAge);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void EnabledRuleUsesConfiguredMinutes(int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), InventoryHeartbeatFreshnessRule.Resolve([CreateRule(minutes)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidRuleFailsExplicitly(int minutes)
    {
        Assert.Throws<InvalidOperationException>(() =>
            InventoryHeartbeatFreshnessRule.Resolve([CreateRule(minutes)]));
    }

    [Fact]
    public void DuplicateGlobalRulesFailExplicitly()
    {
        Assert.Throws<InvalidOperationException>(() =>
            InventoryHeartbeatFreshnessRule.Resolve([CreateRule(5), CreateRule(10, enabled: false)]));
    }

    [Fact]
    public async Task SavedEditsDisableAndDeleteTakeEffectWithoutRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = await CreateDatabaseAsync(connection);
        var store = CreateStore(factory);
        var notifications = 0;
        store.Changed += () => notifications++;

        var input = CreateRule(10);
        input.AlertNameContains = "ignored";
        input.QueryResultType = "ignored";
        input.ConditionType = AlertRuleConditionTypes.RowCountGreaterThan;
        input.CategoryName = "ignored";
        input.IsCritical = true;
        var rule = await store.SaveAlertRuleAsync(input);
        Assert.Equal(TimeSpan.FromMinutes(10),
            InventoryHeartbeatFreshnessRule.Resolve(await store.GetAllAlertRulesAsync()));
        Assert.Empty(rule.ConditionType);
        Assert.Empty(rule.QueryResultType);
        Assert.Empty(rule.AlertNameContains);
        Assert.Empty(rule.CategoryName);
        Assert.False(rule.IsCritical);
        Assert.Equal(0, rule.Priority);

        rule.Threshold = 2;
        await store.SaveAlertRuleAsync(rule);
        Assert.Equal(TimeSpan.FromMinutes(2),
            InventoryHeartbeatFreshnessRule.Resolve(await store.GetAllAlertRulesAsync()));

        rule.Enabled = false;
        await store.SaveAlertRuleAsync(rule);
        Assert.Equal(TimeSpan.FromMinutes(5),
            InventoryHeartbeatFreshnessRule.Resolve(await store.GetAllAlertRulesAsync()));

        Assert.True(await store.DeleteAlertRuleAsync(rule.Id));
        Assert.Equal(TimeSpan.FromMinutes(5),
            InventoryHeartbeatFreshnessRule.Resolve(await store.GetAllAlertRulesAsync()));
        Assert.Equal(4, notifications);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SavingNonPositiveLimitIsRejected(int minutes)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = await CreateDatabaseAsync(connection);
        var store = CreateStore(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAlertRuleAsync(CreateRule(minutes)));

        Assert.Contains("at least 1 minute", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await store.GetAllAlertRulesAsync());
    }

    [Fact]
    public async Task StoreAndDatabaseEnforceOneGlobalRuleEvenWhenDisabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = await CreateDatabaseAsync(connection);
        var store = CreateStore(factory);
        await store.SaveAlertRuleAsync(CreateRule(10, enabled: false));
        var duplicate = CreateRule(20);
        duplicate.Name = "Another heartbeat limit";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAlertRuleAsync(duplicate));
        Assert.Contains("Only one global", exception.Message, StringComparison.Ordinal);

        await using var context = factory.CreateDbContext();
        context.AlertRules.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private static AlertRule CreateRule(int minutes, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Inventory heartbeat freshness",
        RuleType = AlertRuleTypes.InventoryHeartbeatFreshness,
        Enabled = enabled,
        Threshold = minutes
    };

    private static AlertStore CreateStore(IDbContextFactory<AlertDbContext> factory)
    {
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        return new AlertStore(
            factory,
            new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
            presenter,
            new AlertRuleEvaluator(presenter),
            NullLogger<AlertStore>.Instance);
    }

    private static async Task<IDbContextFactory<AlertDbContext>> CreateDatabaseAsync(SqliteConnection connection)
    {
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await using var context = factory.CreateDbContext();
        var tableNames = new[] { "AlertRules", "Alerts", "ParsedAlerts" };
        var statements = context.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(statement => tableNames.Any(name =>
                statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal)) ||
                statement.Contains(" ON \"AlertRules\" ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, statements.Count(statement => statement.TrimStart().StartsWith("CREATE TABLE", StringComparison.Ordinal)));
        foreach (var statement in statements)
        {
            // SQLite does not accept the SQL Server-specific unbounded string type.
            await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
        }
        return factory;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }
}
