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

    private static ParsedAlertRecord CreateParsedAlert(
        string alertId,
        string condition,
        DateTimeOffset receivedAt,
        System.Text.Json.Nodes.JsonObject payload)
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
                Target = source.Target,
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
}
