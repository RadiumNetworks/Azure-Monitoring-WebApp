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

    private static AlertRecord CreateAlert(AlertRuleAlertCase source) => new(
        Guid.NewGuid(), new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero), source.AlertId,
        source.Name, "Sev1", string.Empty, "Log", "Fired", source.Target, "rg-test", "sub-test",
        null, string.Empty, string.Empty, string.Empty, source.Payload.ToJsonString())
    {
        DisplayIdentity = new AlertDisplayIdentity(source.Target, source.Site)
    };
}
