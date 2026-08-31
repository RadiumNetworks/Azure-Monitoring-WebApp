namespace MonitoringApp.Tests;

public sealed class ParsedAlertLifecycleTests
{
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));

    [Fact]
    public void MarksSystemOutageLifecycleCriticalAndStoresResolutionTime()
    {
        var firedAt = DateTimeOffset.Parse("2026-08-31T01:00:00Z");
        var resolvedAt = DateTimeOffset.Parse("2026-08-31T02:15:00Z");
        var alertId = "critical-lifecycle";
        var fired = CreateParsedAlert(alertId, "Fired", firedAt, SystemOutagePayload());
        var resolved = CreateParsedAlert(alertId, "Resolved", resolvedAt, "{}");
        resolved.ResolvedAt = resolvedAt;
        var rule = new AlertRule
        {
            Id = Guid.NewGuid(),
            Name = "System outage",
            Enabled = true,
            RuleType = AlertRuleTypes.Categorization,
            Priority = 10,
            AlertNameContains = "Port",
            QueryResultType = "DCPort",
            ConditionType = AlertRuleConditionTypes.RowCountGreaterThan,
            Threshold = 10,
            CategoryName = "System Outage",
            IsCritical = true
        };

        ParsedAlertLifecycle.Synchronize([fired, resolved], [rule], new AlertRuleEvaluator(Presenter));

        Assert.All([fired, resolved], record => Assert.True(record.IsCritical));
        Assert.All([fired, resolved], record => Assert.Equal(resolvedAt, record.ResolvedAt));
    }

    [Fact]
    public void ClearsCriticalityWhenNoCriticalRuleMatches()
    {
        var parsed = CreateParsedAlert("standard-lifecycle", "Fired", DateTimeOffset.UtcNow, "{}");
        parsed.IsCritical = true;

        ParsedAlertLifecycle.Synchronize([parsed], [], new AlertRuleEvaluator(Presenter));

        Assert.False(parsed.IsCritical);
        Assert.Null(parsed.ResolvedAt);
    }

    private static ParsedAlertRecord CreateParsedAlert(
        string alertId,
        string condition,
        DateTimeOffset receivedAt,
        string rawJson)
    {
        var alert = new AlertRecord(
            Guid.NewGuid(), receivedAt, alertId, "Port health alert", "Sev0", condition, "Log",
            condition, "DC-01", "rg-test", "sub-test", receivedAt, string.Empty, string.Empty,
            string.Empty, rawJson);
        var parsed = ParsedAlertFactory.Create(alert);
        parsed.Alert = alert;
        return parsed;
    }

    private static string SystemOutagePayload() =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            queryResult = new
            {
                type = "DCPort",
                rows = Enumerable.Range(1, 11).Select(value => new[] { value }).ToArray()
            }
        });
}
