using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class ParsedAlertLifecycleTests
{
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
    private static readonly ParsedAlertLifecycleTestCases TestCases =
        TestCaseLoader.Load<ParsedAlertLifecycleTestCases>("parsed-alert-lifecycle.json");

    [Fact]
    public void MarksSystemOutageLifecycleCriticalAndStoresResolutionTime()
    {
        var fired = CreateParsedAlert(
            TestCases.CriticalAlertId,
            "Fired",
            TestCases.FiredAt,
            TestCases.FiredPayload);
        var resolved = CreateParsedAlert(
            TestCases.CriticalAlertId,
            "Resolved",
            TestCases.ResolvedAt,
            []);
        resolved.ResolvedAt = TestCases.ResolvedAt;

        ParsedAlertLifecycle.Synchronize(
            [fired, resolved],
            [TestCases.CriticalRule],
            new AlertRuleEvaluator(Presenter));

        Assert.All([fired, resolved], record => Assert.True(record.IsCritical));
        Assert.All([fired, resolved], record => Assert.Equal(TestCases.ResolvedAt, record.ResolvedAt));
    }

    [Fact]
    public void ClearsCriticalityWhenNoCriticalRuleMatches()
    {
        var parsed = CreateParsedAlert(
            TestCases.StandardAlertId,
            "Fired",
            TestCases.FiredAt,
            []);
        parsed.IsCritical = true;

        ParsedAlertLifecycle.Synchronize([parsed], [], new AlertRuleEvaluator(Presenter));

        Assert.False(parsed.IsCritical);
        Assert.Null(parsed.ResolvedAt);
    }

    [Fact]
    public void MarksHistoricalDomainOutageParticipantsCritical()
    {
        var first = CreateParsedAlert(
            "domain-alert-1",
            "Fired",
            TestCases.FiredAt,
            [],
            "dc-1");
        var second = CreateParsedAlert(
            "domain-alert-2",
            "Fired",
            TestCases.FiredAt.AddMinutes(1),
            [],
            "dc-2");
        var resolved = CreateParsedAlert(
            "domain-alert-1",
            "Resolved",
            TestCases.ResolvedAt,
            [],
            "dc-1");
        resolved.ResolvedAt = TestCases.ResolvedAt;
        var rule = new AlertRule
        {
            Id = Guid.NewGuid(),
            Name = "Domain Service Outage",
            Enabled = true,
            Priority = 10,
            RuleType = AlertRuleTypes.Categorization,
            ConditionType = AlertRuleConditionTypes.DomainControllerPercentageInDomain,
            Threshold = 50,
            CategoryName = "Domain Service Outage",
            IsCritical = true,
            AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage
        };
        var inventory = new[]
        {
            CreateDomainController("dc-1"),
            CreateDomainController("dc-2"),
            CreateDomainController("dc-3")
        };

        ParsedAlertLifecycle.Synchronize(
            [first, second, resolved],
            [rule],
            new AlertRuleEvaluator(Presenter),
            inventory);

        Assert.All([first, second, resolved], record => Assert.True(record.IsCritical));
        Assert.Equal(TestCases.ResolvedAt, first.ResolvedAt);
        Assert.Null(second.ResolvedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentSuppressionDoesNotEraseDirectCriticalHistory(bool markedCritical)
    {
        var parsed = CreateParsedAlert("suppressed", "Fired", TestCases.FiredAt, TestCases.FiredPayload);
        parsed.IsCritical = true;
        var suppression = new AlertRule
        {
            Name = "Quiet evidence", Priority = 999,
            QueryResultType = "DCPort", ConditionType = AlertRuleConditionTypes.AnyMatchingAlert,
            AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed,
            IsCritical = markedCritical
        };

        ParsedAlertLifecycle.Synchronize([parsed], [TestCases.CriticalRule, suppression], new AlertRuleEvaluator(Presenter));

        Assert.True(parsed.IsCritical);
        var counts = CriticalAlertTimeline.GetHourlyCounts(
            [new(parsed.AlertId, parsed.FiredDateTime!.Value, null)],
            TestCases.FiredAt, 3, TestCases.FiredAt.AddHours(3), [parsed.AlertId]);
        Assert.Equal([1, 1, 0], counts.Select(bucket => bucket.Count));
        suppression.Enabled = false;
        ParsedAlertLifecycle.Synchronize([parsed], [TestCases.CriticalRule, suppression], new AlertRuleEvaluator(Presenter));
        Assert.True(parsed.IsCritical);
    }

    [Fact]
    public void HealthyResolutionDoesNotEraseAGenuineHistoricalCriticalPeak()
    {
        var fired = CreateParsedAlert("genuine", "Fired", TestCases.FiredAt, TestCases.FiredPayload);
        var resolved = CreateParsedAlert("genuine", "Resolved", TestCases.ResolvedAt,
            System.Text.Json.Nodes.JsonNode.Parse("""{"queryResult":{"type":"DCPort","rows":[]}}""")!.AsObject());
        var rules = new[] { TestCases.CriticalRule, DCPortSuppressionRuleTests.CreateDefaultRule() };

        ParsedAlertLifecycle.Synchronize([fired, resolved], rules, new AlertRuleEvaluator(Presenter));

        Assert.True(fired.IsCritical);
        Assert.Equal(TestCases.ResolvedAt, fired.ResolvedAt);
        var counts = CriticalAlertTimeline.GetHourlyCounts(
            [new(fired.AlertId, fired.FiredDateTime!.Value, fired.ResolvedAt)],
            TestCases.FiredAt, 3, TestCases.FiredAt.AddHours(3));
        Assert.Equal([1, 0, 0], counts.Select(bucket => bucket.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentSuppressionChangesNowWithoutClearingHistoricalLifecycleMetadata(bool expandToTarget)
    {
        var fired = CreateParsedAlert("initially-critical", "Fired", TestCases.FiredAt, TestCases.FiredPayload);
        fired.IsCritical = true;
        var healthy = CreateParsedAlert(expandToTarget ? "target-suppression" : fired.AlertId,
            "Fired", TestCases.FiredAt.AddMinutes(1),
            System.Text.Json.Nodes.JsonNode.Parse("""{"queryResult":{"type":"DCPort","rows":[]}}""")!.AsObject());
        var suppression = DCPortSuppressionRuleTests.CreateDefaultRule();
        suppression.ApplyToTarget = expandToTarget;

        ParsedAlertLifecycle.Synchronize([fired, healthy],
            [TestCases.CriticalRule, suppression], new AlertRuleEvaluator(Presenter));

        Assert.True(fired.IsCritical);
        Assert.Equal(!expandToTarget, healthy.IsCritical);
        Assert.Null(fired.ResolvedAt);
        Assert.Null(healthy.ResolvedAt);
        var counts = CriticalAlertTimeline.GetHourlyCounts(
            [new(fired.AlertId, fired.FiredDateTime!.Value, fired.ResolvedAt)],
            TestCases.FiredAt, 3, TestCases.FiredAt.AddHours(3), [fired.AlertId, healthy.AlertId]);
        Assert.Equal([1, 1, 0], counts.Select(bucket => bucket.Count));
    }

    [Fact]
    public void LaterTargetSuppressionDoesNotRewriteAnEarlierResolvedCriticalLifecycle()
    {
        var fired = CreateParsedAlert("earlier", "Fired", TestCases.FiredAt, TestCases.FiredPayload);
        var resolved = CreateParsedAlert("earlier", "Resolved", TestCases.ResolvedAt, []);
        var healthy = CreateParsedAlert("later", "Fired", TestCases.ResolvedAt.AddMinutes(1),
            System.Text.Json.Nodes.JsonNode.Parse("""{"queryResult":{"type":"DCPort","rows":[]}}""")!.AsObject());
        var suppression = DCPortSuppressionRuleTests.CreateDefaultRule();
        suppression.ApplyToTarget = true;

        ParsedAlertLifecycle.Synchronize([fired, resolved, healthy],
            [TestCases.CriticalRule, suppression], new AlertRuleEvaluator(Presenter));

        Assert.True(fired.IsCritical);
        Assert.Equal(TestCases.ResolvedAt, fired.ResolvedAt);
        Assert.False(healthy.IsCritical);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReconciliationRestoresHistoricalFlagsWithoutApplyingCurrentSuppression(bool loaded, bool aggregateRules)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AlertDbContext(
            new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        string[] tables = ["Alerts", "ParsedAlerts", "AlertRules", "ComputerInventory"];
        foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(statement => tables.Any(table =>
                statement.TrimStart().StartsWith($"CREATE TABLE \"{table}\" ", StringComparison.Ordinal))))
            await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                .Replace("{}", "{{}}", StringComparison.Ordinal));
        var empty = System.Text.Json.Nodes.JsonNode.Parse("""{"queryResult":{"type":"DCPort","rows":[]}}""")!.AsObject();
        var records = new[]
        {
            CreateParsedAlert("quiet-1", "Fired", TestCases.FiredAt, empty, "dc-1"),
            CreateParsedAlert("quiet-2", "Fired", TestCases.FiredAt.AddMinutes(1), empty, "dc-2"),
            CreateParsedAlert("failure", "Fired", TestCases.FiredAt.AddMinutes(2), TestCases.FiredPayload, "dc-3")
        };
        foreach (var record in records) record.IsCritical = false;
        context.ParsedAlerts.AddRange(records);
        context.ComputerInventory.AddRange(Enumerable.Range(1, 3).Select(index => CreateDomainController($"dc-{index}")));
        context.AlertRules.AddRange(DefaultAlertRules.Create().Where(rule => aggregateRules ||
            rule.ConditionType is not AlertRuleConditionTypes.DomainControllerPercentageInDomain and
                not AlertRuleConditionTypes.AllDomainControllersInSite));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var evaluator = new AlertRuleEvaluator(Presenter);

        if (loaded)
        {
            var stored = await context.ParsedAlerts.Include(record => record.Alert).ToArrayAsync();
            await ParsedAlertLifecyclePersistence.SynchronizeLoadedAsync(context, evaluator, stored, default);
        }
        else
        {
            await ParsedAlertLifecyclePersistence.SynchronizeAsync(context, evaluator, aggregateRules ? "quiet-1" : null, default);
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var repaired = await context.ParsedAlerts.AsNoTracking().ToArrayAsync();
        Assert.All(repaired.Where(record => record.AlertId.StartsWith("quiet-", StringComparison.Ordinal)),
            record => Assert.Equal(aggregateRules, record.IsCritical));
        Assert.True(repaired.Single(record => record.AlertId == "failure").IsCritical);
        Assert.All(repaired, record => Assert.Null(record.ResolvedAt));
        var lifecycles = repaired.Where(record => record.IsCritical)
            .Select(record => new CriticalAlertLifecycle(record.AlertId, record.FiredDateTime!.Value, record.ResolvedAt));
        var buckets = CriticalAlertTimeline.GetHourlyCounts(
            lifecycles, TestCases.FiredAt, 3, TestCases.FiredAt.AddHours(3), ["QUIET-1", "QUIET-2"]);
        var historicalCount = aggregateRules ? 3 : 1;
        Assert.Equal([historicalCount, historicalCount, 1], buckets.Select(bucket => bucket.Count));
    }

    private static ParsedAlertRecord CreateParsedAlert(
        string alertId,
        string condition,
        DateTimeOffset receivedAt,
        System.Text.Json.Nodes.JsonObject payload,
        string? target = null)
    {
        var source = TestCases.Alert;
        var alert = TestAlertFactory.FromFixture(
            new AlertRecordFixtureCase
            {
                ReceivedAt = receivedAt,
                AlertId = alertId,
                Name = source.Name,
                Severity = source.Severity,
                Status = condition,
                SignalType = source.SignalType,
                MonitorCondition = condition,
                Target = target ?? source.Target,
                ResourceGroup = source.ResourceGroup,
                SubscriptionId = source.SubscriptionId,
                FiredAt = receivedAt,
                Description = source.Description
            },
            payload: payload);
        var parsed = ParsedAlertFactory.Create(alert);
        parsed.Alert = alert;
        return parsed;
    }

    private static ComputerInventoryEntry CreateDomainController(string computer) => new()
    {
        SubscriptionId = TestCases.Alert.SubscriptionId,
        Computer = computer,
        Domain = "contoso.test",
        Site = "site-a",
        Role = "domaincontrollers"
    };
}
