using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class AlertQueryRefreshTests
{
    [Fact]
    public void AcceptsValidEmptySnapshot()
    {
        var queryTime = DateTimeOffset.Parse("2026-10-06T10:00:00Z");
        var request = CreateRequest(queryTime, "[]", 0);

        var errors = AlertQueryRefresh.Validate(request, queryTime.AddMinutes(1));

        Assert.Empty(errors);
    }

    [Fact]
    public void RejectsMismatchedRowCountUnsafeComputerAndFutureTimestamp()
    {
        var now = DateTimeOffset.Parse("2026-10-06T10:00:00Z");
        var request = CreateRequest(now.AddMinutes(6), """[["failed"]]""", 0) with
        {
            DimensionValue = "DC\"01"
        };

        var errors = AlertQueryRefresh.Validate(request, now);

        Assert.Contains(nameof(request.DimensionValue), errors);
        Assert.Contains(nameof(request.RowCount), errors);
        Assert.Contains(nameof(request.QueryExecutedAt), errors);
    }

    [Fact]
    public void ReplacesOnlyRootQueryResult()
    {
        var queryTime = DateTimeOffset.Parse("2026-10-06T10:00:00Z");
        var queryResult = AlertQueryRefresh.CreateQueryResultJson(
            CreateRequest(queryTime, """[["failed"]]""", 1));

        var updated = AlertQueryRefresh.ReplaceQueryResult(
            """
            {
              "schemaId": "azureMonitorCommonAlertSchema",
              "data": { "essentials": { "alertId": "alert-1" } },
              "queryResult": { "type": "DCPort", "computer": "DC01", "rows": [] }
            }
            """,
            queryResult);

        using var document = JsonDocument.Parse(updated);
        Assert.Equal("azureMonitorCommonAlertSchema", document.RootElement.GetProperty("schemaId").GetString());
        Assert.Equal(
            "alert-1",
            document.RootElement.GetProperty("data").GetProperty("essentials").GetProperty("alertId").GetString());
        var refreshed = document.RootElement.GetProperty("queryResult");
        Assert.Equal(1, refreshed.GetProperty("rowCount").GetInt32());
        Assert.Equal(queryTime, refreshed.GetProperty("refreshedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("DCDiag", "computer", "DC01", "Computer")]
    [InlineData("Replication", "computer", "DC01", "Computer")]
    [InlineData("DCPort", "computer", "DC01", "Computer")]
    [InlineData("Heartbeat", "computer", "DC01", "Computer")]
    public void ReadsSupportedRefreshContext(
        string type,
        string contextProperty,
        string value,
        string expectedDimensionName)
    {
        var parsed = AlertQueryRefresh.TryReadContext(
            $$"""{ "type": "{{type}}", "{{contextProperty}}": "{{value}}" }""",
            out var definition,
            out var dimensionValue);

        Assert.True(parsed);
        Assert.Equal(type, definition.Type);
        Assert.Equal(expectedDimensionName, definition.DimensionName);
        Assert.Equal(value, dimensionValue);
    }

    [Theory]
    [InlineData("DCDiag", "computer", "Computer")]
    [InlineData("Replication", "computer", "Computer")]
    [InlineData("DCPort", "computer", "Computer")]
    [InlineData("Heartbeat", "computer", "Computer")]
    public async Task StoreDiscoversAndUpdatesEverySupportedType(
        string type,
        string contextProperty,
        string dimensionName)
    {
        await using var database = await CreateDatabaseAsync();
        var fired = CreateAlert(
            "Fired",
            DateTimeOffset.Parse("2026-10-06T10:00:00Z"),
            type,
            contextProperty,
            dimensionName);
        await SeedAsync(database.Factory, fired);
        var store = CreateStore(database.Factory);

        var targets = await store.GetActiveAlertQueryRefreshTargetsAsync(type);
        var result = await store.UpdateAlertQueryResultAsync(
            fired.Id,
            CreateRequest(
                DateTimeOffset.Parse("2026-10-06T10:05:00Z"),
                "[]",
                0,
                type,
                dimensionName));

        var target = Assert.Single(targets);
        Assert.Equal(type, target.Type);
        Assert.Equal(dimensionName, target.DimensionName);
        Assert.Equal("DC01", target.DimensionValue);
        Assert.Equal(AlertQueryRefreshUpdateStatus.Updated, result.Status);
    }

    [Fact]
    public async Task StoreUpdatesExistingAlertWithoutCreatingDuplicate()
    {
        await using var database = await CreateDatabaseAsync();
        var fired = CreateAlert("Fired", DateTimeOffset.Parse("2026-10-06T10:00:00Z"));
        await SeedAsync(database.Factory, fired);
        var store = CreateStore(database.Factory);
        var queryTime = DateTimeOffset.Parse("2026-10-06T10:05:00Z");
        var request = CreateRequest(queryTime, """[["failed"]]""", 1);

        var targets = await store.GetActiveAlertQueryRefreshTargetsAsync("DCPort");
        var firstResult = await store.UpdateAlertQueryResultAsync(fired.Id, request);
        var repeatedResult = await store.UpdateAlertQueryResultAsync(fired.Id, request);
        var staleResult = await store.UpdateAlertQueryResultAsync(
            fired.Id,
            CreateRequest(queryTime.AddMinutes(-1), "[]", 0));

        var target = Assert.Single(targets);
        Assert.Equal(fired.Id, target.Id);
        Assert.Equal("DC01", target.DimensionValue);
        Assert.Equal(AlertQueryRefreshUpdateStatus.Updated, firstResult.Status);
        Assert.Equal(AlertQueryRefreshUpdateStatus.Unchanged, repeatedResult.Status);
        Assert.Equal(AlertQueryRefreshUpdateStatus.Stale, staleResult.Status);

        await using var context = await database.Factory.CreateDbContextAsync();
        var storedAlert = Assert.Single(await context.Alerts.AsNoTracking().ToArrayAsync());
        var storedParsed = Assert.Single(await context.ParsedAlerts.AsNoTracking().ToArrayAsync());
        var storedHistory = Assert.Single(await context.AlertHistory.AsNoTracking().ToArrayAsync());
        using var rawJson = JsonDocument.Parse(storedAlert.RawJson);
        using var historyJson = JsonDocument.Parse(storedHistory.RawJson);
        Assert.Equal(
            1,
            rawJson.RootElement.GetProperty("queryResult").GetProperty("rowCount").GetInt32());
        Assert.Equal(
            0,
            historyJson.RootElement.GetProperty("queryResult").GetProperty("rowCount").GetInt32());
        Assert.Equal(queryTime, storedParsed.QueryResultRefreshedAt);
        Assert.Equal(fired.AlertId, storedAlert.AlertId);
        Assert.Equal(fired.MonitorCondition, storedAlert.MonitorCondition);
        Assert.Equal(fired.ReceivedAt, storedAlert.ReceivedAt);
        Assert.Equal(fired.FiredAt, storedAlert.FiredAt);
        Assert.Equal(fired.Comments, storedAlert.Comments);
        Assert.Equal(1, await context.Alerts.CountAsync());
        Assert.Equal(1, await context.ParsedAlerts.CountAsync());
    }

    [Fact]
    public async Task SavedDCPortRuleSuppressesHealthyRefreshAndRestoresFailingAlert()
    {
        await using var database = await CreateDatabaseAsync();
        await using (var context = database.Factory.CreateDbContext())
        {
            var ruleTable = context.Database.GenerateCreateScript()
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Single(statement => statement.TrimStart().StartsWith(
                    "CREATE TABLE \"AlertRules\" ", StringComparison.Ordinal));
            await context.Database.ExecuteSqlRawAsync(ruleTable);
        }
        var fired = CreateAlert("Fired", DateTimeOffset.Parse("2026-10-09T10:00:00Z"));
        await SeedAsync(database.Factory, fired);
        var store = CreateStore(database.Factory);
        var changed = 0;
        store.Changed += () => changed++;
        var rule = await store.SaveAlertRuleAsync(DCPortSuppressionRuleTests.CreateDefaultRule());
        Assert.Equal(AlertRuleConditionTypes.RowCountEquals, rule.ConditionType);
        Assert.Equal(0, rule.Threshold);
        var evaluator = new AlertRuleEvaluator(
            new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")));

        var healthyResult = await store.UpdateAlertQueryResultAsync(
            fired.Id, CreateRequest(DateTimeOffset.Parse("2026-10-09T10:05:00Z"), "[]", 0));
        Assert.Equal(AlertQueryRefreshUpdateStatus.Updated, healthyResult.Status);
        await using (var context = database.Factory.CreateDbContext())
        {
            var alert = Assert.Single(await context.Alerts.AsNoTracking().ToArrayAsync());
            Assert.True(evaluator.MatchesRule(rule, alert));
            Assert.Equal("Fired", alert.MonitorCondition);
            Assert.Single(await store.GetActiveAlertQueryRefreshTargetsAsync("DCPort"));
        }

        var failingResult = await store.UpdateAlertQueryResultAsync(
            fired.Id, CreateRequest(DateTimeOffset.Parse("2026-10-09T10:10:00Z"), """[["Failed"]]""", 1));
        Assert.Equal(AlertQueryRefreshUpdateStatus.Updated, failingResult.Status);
        await using (var context = database.Factory.CreateDbContext())
        {
            var alert = Assert.Single(await context.Alerts.AsNoTracking().ToArrayAsync());
            Assert.False(evaluator.MatchesRule(rule, alert));
            Assert.Equal(fired.AlertId, alert.AlertId);
            Assert.Equal("Fired", alert.MonitorCondition);
            Assert.Equal(1, await context.ParsedAlerts.CountAsync());
        }

        rule.Threshold = -1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAlertRuleAsync(rule));
        rule.Threshold = 0;
        rule.Enabled = false;
        await store.SaveAlertRuleAsync(rule);
        Assert.False(Assert.Single(await store.GetAllAlertRulesAsync()).Enabled);
        Assert.Equal(4, changed);
    }

    [Fact]
    public async Task StoreStopsRefreshingAfterResolvedEvent()
    {
        await using var database = await CreateDatabaseAsync();
        var firedAt = DateTimeOffset.Parse("2026-10-06T10:00:00Z");
        var fired = CreateAlert("Fired", firedAt);
        var resolved = CreateAlert("Resolved", firedAt.AddMinutes(4)) with
        {
            Id = Guid.NewGuid()
        };
        await SeedAsync(database.Factory, fired, resolved);
        var store = CreateStore(database.Factory);

        var targets = await store.GetActiveAlertQueryRefreshTargetsAsync("DCPort");
        var result = await store.UpdateAlertQueryResultAsync(
            fired.Id,
            CreateRequest(firedAt.AddMinutes(5), "[]", 0));

        Assert.Empty(targets);
        Assert.Equal(AlertQueryRefreshUpdateStatus.NotActive, result.Status);
    }

    private static UpdateAlertQueryResultRequest CreateRequest(
        DateTimeOffset queryTime,
        string rowsJson,
        int rowCount,
        string type = "DCPort",
        string dimensionName = "Computer") => new()
        {
            Type = type,
            DimensionName = dimensionName,
            DimensionValue = "DC01",
            Columns = Parse("""[{ "name": "Status", "type": "dynamic" }]"""),
            Rows = Parse(rowsJson),
            RowCount = rowCount,
            QueryExecutedAt = queryTime
        };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AlertRecord CreateAlert(
        string monitorCondition,
        DateTimeOffset receivedAt,
        string type = "DCPort",
        string contextProperty = "computer",
        string dimensionName = "Computer") => new(
        Guid.NewGuid(),
        receivedAt,
        "azure-alert-1",
        "DC Port Monitor",
        "Sev2",
        "Activated",
        "Log",
        monitorCondition,
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/test/DC01",
        "rg-1",
        "sub-1",
        receivedAt,
        "Port test",
        string.Empty,
        string.Empty,
        $$"""
        {
          "schemaId": "azureMonitorCommonAlertSchema",
          "data": {
            "essentials": {
              "alertId": "azure-alert-1",
              "monitorCondition": "Fired"
            },
            "alertContext": {
              "condition": {
                "allOf": [
                  {
                    "dimensions": [
                      { "name": "{{dimensionName}}", "value": "DC01" }
                    ]
                  }
                ]
              }
            }
          },
          "queryResult": {
            "type": "{{type}}",
            "{{contextProperty}}": "DC01",
            "columns": [
              { "name": "Status", "type": "dynamic" }
            ],
            "rows": [],
            "rowCount": 0
          }
        }
        """);

    private static async Task SeedAsync(
        IDbContextFactory<AlertDbContext> factory,
        params AlertRecord[] alerts)
    {
        await using var context = factory.CreateDbContext();
        foreach (var alert in alerts)
        {
            context.Alerts.Add(alert);
            context.ParsedAlerts.Add(ParsedAlertFactory.Create(alert));
            if (alert.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase))
            {
                context.AlertHistory.Add(AlertHistoryRecord.Create(alert));
            }
        }

        await context.SaveChangesAsync();
    }

    private static AlertStore CreateStore(IDbContextFactory<AlertDbContext> factory)
    {
        var definitionsPath = Path.Combine(AppContext.BaseDirectory, "AlertDefinitions");
        var presenter = new QueryResultPresenter(definitionsPath);
        return new AlertStore(
            factory,
            new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()),
            presenter,
            new AlertRuleEvaluator(presenter),
            NullLogger<AlertStore>.Instance);
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        await using var context = factory.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE Alerts (
                Id TEXT NOT NULL PRIMARY KEY,
                ReceivedAt TEXT NOT NULL,
                AlertId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Severity TEXT NOT NULL,
                Status TEXT NOT NULL,
                SignalType TEXT NOT NULL,
                MonitorCondition TEXT NOT NULL,
                Target TEXT NOT NULL,
                ResourceGroup TEXT NOT NULL,
                SubscriptionId TEXT NOT NULL,
                FiredAt TEXT NULL,
                Description TEXT NOT NULL,
                SearchResultsUrl TEXT NOT NULL,
                Comments TEXT NOT NULL,
                RawJson TEXT NOT NULL
            );

            CREATE TABLE ParsedAlerts (
                Id TEXT NOT NULL PRIMARY KEY,
                FiredDateTime TEXT NULL,
                AlertId TEXT NOT NULL,
                OriginalAlertId TEXT NOT NULL,
                Severity TEXT NOT NULL,
                MonitorCondition TEXT NOT NULL,
                Dimensions TEXT NOT NULL,
                SearchQuery TEXT NOT NULL,
                QueryResults TEXT NOT NULL,
                QueryResultRefreshedAt TEXT NULL,
                AlertName TEXT NOT NULL,
                ResourceGroup TEXT NOT NULL,
                IsCritical INTEGER NOT NULL,
                ResolvedAt TEXT NULL,
                InventorySubscriptionId TEXT NULL,
                InventoryComputer TEXT NULL,
                FOREIGN KEY (Id) REFERENCES Alerts(Id) ON DELETE CASCADE
            );

            CREATE TABLE AlertHistory (
                Id TEXT NOT NULL PRIMARY KEY,
                ReceivedAt TEXT NOT NULL,
                AlertId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Severity TEXT NOT NULL,
                Status TEXT NOT NULL,
                SignalType TEXT NOT NULL,
                MonitorCondition TEXT NOT NULL,
                Target TEXT NOT NULL,
                ResourceGroup TEXT NOT NULL,
                SubscriptionId TEXT NOT NULL,
                FiredAt TEXT NULL,
                Description TEXT NOT NULL,
                SearchResultsUrl TEXT NOT NULL,
                Comments TEXT NOT NULL,
                RawJson TEXT NOT NULL
            );
            """);
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options)
        : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class TestDatabase(
        SqliteConnection connection,
        IDbContextFactory<AlertDbContext> factory) : IAsyncDisposable
    {
        public IDbContextFactory<AlertDbContext> Factory { get; } = factory;

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
