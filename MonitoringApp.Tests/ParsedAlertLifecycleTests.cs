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
