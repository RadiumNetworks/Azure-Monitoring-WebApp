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
    public void IgnoresInventoryRoleAssignmentRulesWhenCategorizing()
    {
        var alert = CreateAlert(new AlertRuleAlertCase
        {
            AlertId = "role-alert",
            Name = "DCDiag alert",
            Target = "dc01.contoso.test"
        });
        var rule = new AlertRule
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            RuleType = AlertRuleTypes.InventoryRoleAssignment,
            QueryResultType = "DCDiag",
            InventoryRole = "domaincontrollers"
        };

        var result = new AlertRuleEvaluator(Presenter).Categorize([alert], [rule]);

        Assert.Empty(result.Categories);
        Assert.Same(alert, Assert.Single(result.Uncategorized));
    }

    private static AlertRecord CreateAlert(AlertRuleAlertCase source) => new(
        Guid.NewGuid(), new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero), source.AlertId,
        source.Name, "Sev1", string.Empty, "Log", "Fired", source.Target, "rg-test", "sub-test",
        null, string.Empty, string.Empty, string.Empty, source.Payload.ToJsonString())
    {
        DisplayIdentity = new AlertDisplayIdentity(source.Target, source.Site)
    };
}
