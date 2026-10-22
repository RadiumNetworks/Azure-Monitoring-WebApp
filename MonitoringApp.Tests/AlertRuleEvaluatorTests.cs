namespace MonitoringApp.Tests;

public sealed class AlertRuleEvaluatorTests
{
    private static readonly AlertRuleEvaluatorTestCases Cases =
        TestCaseLoader.Load<AlertRuleEvaluatorTestCases>("alert-rule-evaluator.json");
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));

    public static IEnumerable<object[]> CaseIndexes =>
        Enumerable.Range(0, Cases.Cases.Count).Select(index => new object[] { index });

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void CategorizesAlertsUsingJsonRuleCases(int caseIndex)
    {
        var testCase = Cases.Cases[caseIndex];
        var alerts = testCase.Alerts.Select(CreateAlert).ToArray();

        var result = new AlertRuleEvaluator(Presenter).Categorize(alerts, [testCase.Rule]);

        Assert.Equal(testCase.ExpectedCategories.Count, result.Categories.Count);
        foreach (var expected in testCase.ExpectedCategories)
        {
            var actual = Assert.Single(result.Categories, category => category.Name == expected.Name);
            Assert.Equal(expected.Collapsed, actual.Collapsed);
            Assert.Equal(expected.AlertIds, actual.Alerts.Select(alert => alert.AlertId));
        }

        Assert.Equal(testCase.ExpectedUncategorizedAlertIds, result.Uncategorized.Select(alert => alert.AlertId));
    }

    [Fact]
    public void MatchesInventoryStatusEventByAlertNameWithoutQueryResult()
    {
        var rule = new AlertRule
        {
            Enabled = true,
            RuleType = AlertRuleTypes.Suppression,
            AlertNameContains = "Inventory",
            ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
        };
        var alert = CreateAlert(new AlertRuleAlertCase
        {
            AlertId = "status-event",
            Name = "Domain Controller Inventory Status",
            Target = "dc01",
            Payload = []
        });

        var evaluator = new AlertRuleEvaluator(Presenter);

        Assert.True(evaluator.MatchesSuppressionRule(rule, alert));
        Assert.Empty(evaluator.Categorize([alert], [rule]).Categories);
    }

    [Fact]
    public void MatchesInventoryStatusRuleByQueryResultType()
    {
        var rule = new AlertRule
        {
            Enabled = true,
            RuleType = AlertRuleTypes.InventoryStatus,
            QueryResultType = InventoryHeartbeatBatch.QueryResultType,
            ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
        };
        var source = new AlertRuleAlertCase
        {
            AlertId = "inventory-heartbeat",
            Name = "Inventory Heartbeat Status",
            Target = string.Empty,
            Payload = new System.Text.Json.Nodes.JsonObject
            {
                ["queryResult"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = InventoryHeartbeatBatch.QueryResultType,
                    ["rows"] = new System.Text.Json.Nodes.JsonArray()
                }
            }
        };

        Assert.True(new AlertRuleEvaluator(Presenter).MatchesInventoryStatusRule(rule, CreateAlert(source)));
    }

    [Fact]
    public void ExplainsWinningAndSupersededRulesWithUsedFields()
    {
        var alert = CreateAlert(new AlertRuleAlertCase
        {
            AlertId = "port-alert",
            Name = "Port Alert",
            Target = "server-1",
            Payload = new System.Text.Json.Nodes.JsonObject
            {
                ["queryResult"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "DCPort",
                    ["rows"] = new System.Text.Json.Nodes.JsonArray(
                        Enumerable.Range(0, 11).Select(_ => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray()).ToArray())
                }
            }
        });
        var winner = CreatePortRule("11111111-1111-1111-1111-111111111111", 10);
        var superseded = CreatePortRule("22222222-2222-2222-2222-222222222222", 20);

        var result = new AlertRuleEvaluator(Presenter).EvaluateCategorization(alerts: [alert], rules: [superseded, winner]);

        Assert.Equal(winner.Id, Assert.Single(result.Categorization.Categories).RuleId);
        var applied = Assert.Single(result.Decisions, decision => decision.RuleId == winner.Id);
        Assert.Equal(AlertRuleEvaluationOutcomes.Applied, applied.Outcome);
        Assert.True(applied.DirectMatch);
        Assert.Equal(winner.Id, applied.WinningRuleId);
        Assert.Collection(
            applied.Criteria,
            criterion => Assert.Equal("AlertName", criterion.Field),
            criterion => Assert.Equal("QueryResultType", criterion.Field),
            criterion =>
            {
                Assert.Equal("QueryResultRowCount", criterion.Field);
                Assert.Equal("10", criterion.Expected);
                Assert.Equal("11", criterion.Actual);
                Assert.True(criterion.Matched);
            });

        var skipped = Assert.Single(result.Decisions, decision => decision.RuleId == superseded.Id);
        Assert.Equal(AlertRuleEvaluationOutcomes.Superseded, skipped.Outcome);
        Assert.Equal(winner.Id, skipped.WinningRuleId);
    }

    [Fact]
    public void TargetExpansionDoesNotCrossSubscriptionBoundary()
    {
        var trigger = CreateAlert(new AlertRuleAlertCase
        {
            AlertId = "trigger",
            Name = "Port Alert",
            Target = "server-1",
            Payload = PortPayload(11)
        }) with { SubscriptionId = "sub-a" };
        var sameSubscription = CreateAlert(new AlertRuleAlertCase
        {
            AlertId = "same-subscription",
            Name = "Heartbeat Alert",
            Target = "server-1",
            Payload = []
        }) with { SubscriptionId = "sub-a" };
        var otherSubscription = CreateAlert(new AlertRuleAlertCase
        {
            AlertId = "other-subscription",
            Name = "Heartbeat Alert",
            Target = "server-1",
            Payload = []
        }) with { SubscriptionId = "sub-b" };
        var rule = CreatePortRule("33333333-3333-3333-3333-333333333333", 10);

        var result = new AlertRuleEvaluator(Presenter).EvaluateCategorization(
            [trigger, sameSubscription, otherSubscription], [rule]);

        var category = Assert.Single(result.Categorization.Categories);
        Assert.Equal(["trigger", "same-subscription"], category.Alerts.Select(alert => alert.AlertId));
        var expanded = Assert.Single(result.Decisions, decision => decision.AlertId == "same-subscription");
        Assert.Equal(AlertRuleEvaluationOutcomes.Applied, expanded.Outcome);
        Assert.False(expanded.DirectMatch);
        Assert.Equal(trigger.Id, expanded.TriggerEventId);
        var excluded = Assert.Single(result.Decisions, decision => decision.AlertId == "other-subscription");
        Assert.Equal(AlertRuleEvaluationOutcomes.NotMatched, excluded.Outcome);
    }

    [Fact]
    public void CategorizesDomainOutageOnlyAboveConfiguredPercentage()
    {
        var dc1Alert = CreateDcAlert("dc1-alert", "dc-1", "site-a");
        var dc2Alert = CreateDcAlert("dc2-alert", "dc-2", "site-a");
        var inventory = new[]
        {
            CreateDomainController("dc-1", "site-a"),
            CreateDomainController("dc-2", "site-a"),
            CreateDomainController("dc-3", "site-b"),
            CreateDomainController("dc-4", "site-b")
        };
        var rule = CreateAggregateRule(
            "44444444-4444-4444-4444-444444444444",
            10,
            AlertRuleConditionTypes.DomainControllerPercentageInDomain,
            50,
            "Domain Service Outage");
        var evaluator = new AlertRuleEvaluator(Presenter);

        var atThreshold = evaluator.Categorize([dc1Alert, dc2Alert], [rule], inventory);
        var aboveThreshold = evaluator.Categorize(
            [dc1Alert, dc2Alert],
            [rule],
            inventory,
            [dc1Alert, dc2Alert, CreateDcAlert("dc3-alert", "dc-3", "site-b")]);

        Assert.Empty(atThreshold.Categories);
        Assert.Equal(
            ["dc1-alert", "dc2-alert"],
            Assert.Single(aboveThreshold.Categories).Alerts.Select(alert => alert.AlertId));
    }

    [Fact]
    public void CategorizesSiteOutageOnlyWhenEveryDomainControllerInSiteHasAnAlert()
    {
        var dc1Alert = CreateDcAlert("dc1-alert", "dc-1", "site-a");
        var dc2Alert = CreateDcAlert("dc2-alert", "dc-2", "site-a");
        var dc3Alert = CreateDcAlert("dc3-alert", "dc-3", "site-b");
        var inventory = new[]
        {
            CreateDomainController("dc-1", "site-a"),
            CreateDomainController("dc-2", "site-a"),
            CreateDomainController("dc-3", "site-b"),
            CreateDomainController("dc-4", "site-b")
        };
        var rule = CreateAggregateRule(
            "55555555-5555-5555-5555-555555555555",
            20,
            AlertRuleConditionTypes.AllDomainControllersInSite,
            0,
            "Domain Service Site Outage");

        var result = new AlertRuleEvaluator(Presenter).Categorize(
            [dc1Alert, dc2Alert, dc3Alert],
            [rule],
            inventory);

        var category = Assert.Single(result.Categories);
        Assert.Equal(["dc1-alert", "dc2-alert"], category.Alerts.Select(alert => alert.AlertId));
        Assert.Equal(["dc3-alert"], result.Uncategorized.Select(alert => alert.AlertId));
    }

    [Fact]
    public void DomainOutageSupersedesSiteAndSystemOutageRules()
    {
        var dc1Alert = CreateDcAlert("dc1-alert", "dc-1", "site-a", PortPayload(11));
        var dc2Alert = CreateDcAlert("dc2-alert", "dc-2", "site-a", PortPayload(11));
        var inventory = new[]
        {
            CreateDomainController("dc-1", "site-a"),
            CreateDomainController("dc-2", "site-a"),
            CreateDomainController("dc-3", "site-b")
        };
        var domainRule = CreateAggregateRule(
            "66666666-6666-6666-6666-666666666666",
            10,
            AlertRuleConditionTypes.DomainControllerPercentageInDomain,
            50,
            "Domain Service Outage");
        var siteRule = CreateAggregateRule(
            "77777777-7777-7777-7777-777777777777",
            20,
            AlertRuleConditionTypes.AllDomainControllersInSite,
            0,
            "Domain Service Site Outage");
        var systemRule = CreatePortRule("88888888-8888-8888-8888-888888888888", 30);

        var result = new AlertRuleEvaluator(Presenter).EvaluateCategorization(
            [dc1Alert, dc2Alert],
            [systemRule, siteRule, domainRule],
            inventory);

        Assert.Equal(domainRule.Id, Assert.Single(result.Categorization.Categories).RuleId);
        Assert.All(
            result.Decisions.Where(decision => decision.RuleId != domainRule.Id && decision.DirectMatch),
            decision => Assert.Equal(AlertRuleEvaluationOutcomes.Superseded, decision.Outcome));
    }

    private static AlertRule CreatePortRule(string id, int priority) => new()
    {
        Id = Guid.Parse(id),
        Name = $"Port outage {priority}",
        Enabled = true,
        Priority = priority,
        RuleType = AlertRuleTypes.Categorization,
        AlertNameContains = "Port",
        QueryResultType = "DCPort",
        ConditionType = AlertRuleConditionTypes.RowCountGreaterThan,
        Threshold = 10,
        CategoryName = "Editable display name",
        ApplyToTarget = true,
        AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage
    };

    private static AlertRule CreateAggregateRule(
        string id,
        int priority,
        string conditionType,
        int threshold,
        string categoryName) => new()
    {
        Id = Guid.Parse(id),
        Name = categoryName,
        Enabled = true,
        Priority = priority,
        RuleType = AlertRuleTypes.Categorization,
        ConditionType = conditionType,
        Threshold = threshold,
        CategoryName = categoryName,
        Tone = "failure"
    };

    private static ComputerInventoryEntry CreateDomainController(string computer, string site) => new()
    {
        SubscriptionId = "sub-test",
        Computer = computer,
        Domain = "contoso.test",
        Site = site,
        Role = "Domain Controller"
    };

    private static AlertRecord CreateDcAlert(
        string alertId,
        string target,
        string site,
        System.Text.Json.Nodes.JsonObject? payload = null) =>
        CreateAlert(new AlertRuleAlertCase
        {
            AlertId = alertId,
            Name = payload is null ? "DC health alert" : "Port Alert",
            Target = target,
            Site = site,
            Payload = payload ?? []
        });

    private static System.Text.Json.Nodes.JsonObject PortPayload(int rowCount) => new()
    {
        ["queryResult"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "DCPort",
            ["rows"] = new System.Text.Json.Nodes.JsonArray(
                Enumerable.Range(0, rowCount).Select(_ => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray()).ToArray())
        }
    };

    private static AlertRecord CreateAlert(AlertRuleAlertCase source) => new(
        Guid.NewGuid(), new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero), source.AlertId,
        source.Name, "Sev1", string.Empty, "Log", "Fired", source.Target, "rg-test", "sub-test",
        null, string.Empty, string.Empty, string.Empty, source.Payload.ToJsonString())
    {
        DisplayIdentity = new AlertDisplayIdentity(source.Target, source.Site)
    };
}
