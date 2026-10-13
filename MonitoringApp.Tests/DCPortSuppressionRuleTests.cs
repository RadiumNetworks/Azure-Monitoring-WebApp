using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MonitoringApp.Migrations;

namespace MonitoringApp.Tests;

public sealed class DCPortSuppressionRuleTests
{
    private static readonly AlertRuleEvaluator Evaluator = new(
        new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")));

    [Fact]
    public void MigrationSeedsEditableCollapsedSuppressedCategory()
    {
        var rule = CreateDefaultRule();

        Assert.Equal("Suppress healthy DCPort results", rule.Name);
        Assert.True(rule.Enabled);
        Assert.Equal(5, rule.Priority);
        Assert.Equal(AlertRuleTypes.Categorization, rule.RuleType);
        Assert.Empty(rule.AlertNameContains);
        Assert.Equal("DCPort", rule.QueryResultType);
        Assert.Equal(AlertRuleConditionTypes.RowCountEquals, rule.ConditionType);
        Assert.Equal(0, rule.Threshold);
        Assert.Equal("Suppressed alerts", rule.CategoryName);
        Assert.True(rule.Collapsed);
        Assert.False(rule.ApplyToTarget);
        Assert.False(rule.IsCritical);
        Assert.Equal(AlertRuleAnalysisClassifications.Suppressed, rule.AnalysisClassification);
    }

    [Theory]
    [InlineData("""{"queryResult":{"type":"DCPort","columns":[],"rows":[],"rowCount":0}}""", true)]
    [InlineData("""{"queryResult":{"type":"dcport","columns":[],"rows":[]}}""", true)]
    [InlineData("""{"queryResult":{"type":"DCPort","columns":[{"name":"Status"}],"rows":[["Failed"]]}}""", false)]
    [InlineData("""{"queryResult":{"type":"DCPort","columns":[],"rows":[[]],"rowCount":0}}""", false)]
    [InlineData("""{"queryResult":{"type":"Replication","columns":[],"rows":[]}}""", false)]
    [InlineData("""{"queryResult":{"type":"DCDiag","columns":[],"rows":[]}}""", false)]
    [InlineData("""{"queryResult":{"type":"DCPort","rowCount":0}}""", false)]
    [InlineData("""{"queryResult":{"type":"DCPort","rows":null}}""", false)]
    [InlineData("""{"queryResult":{"type":"DCPort","rows":{}}}""", false)]
    [InlineData("""{"queryResult":{"rows":[]}}""", false)]
    [InlineData("{}", false)]
    [InlineData("invalid JSON", false)]
    public void MatchesOnlyExplicitZeroCheckDCPortResult(string rawJson, bool expected)
    {
        var alert = CreateAlert(rawJson);
        var rule = CreateDefaultRule();

        var result = Evaluator.EvaluateCategorization([alert], [rule]);

        Assert.Equal(expected, result.Categorization.Categories.Count == 1);
        Assert.Equal(expected ? 0 : 1, result.Categorization.Uncategorized.Count);
        if (expected)
        {
            var category = Assert.Single(result.Categorization.Categories);
            Assert.Equal("Suppressed alerts", category.Name);
            Assert.True(category.Collapsed);
            Assert.Equal(alert.Id, Assert.Single(category.Alerts).Id);
            var criterion = Assert.Single(
                Assert.Single(result.Decisions).Criteria,
                item => item.Field == "QueryResultRowCount");
            Assert.Equal("0", criterion.Expected);
            Assert.Equal("0", criterion.Actual);
            Assert.Equal(AlertRuleConditionTypes.RowCountEquals, criterion.Operation);
        }
    }

    [Fact]
    public void RuleEditsAndDisableChangeMatching()
    {
        var empty = CreateAlert("""{"queryResult":{"type":"DCPort","rows":[]}}""");
        var failing = CreateAlert("""{"queryResult":{"type":"DCPort","rows":[["Failed"]]}}""");
        var rule = CreateDefaultRule();
        rule.Threshold = 1;

        Assert.False(Evaluator.MatchesRule(rule, empty));
        Assert.True(Evaluator.MatchesRule(rule, failing));

        rule.Enabled = false;
        Assert.False(Evaluator.MatchesRule(rule, failing));
        Assert.Empty(Evaluator.Categorize([empty, failing], [rule]).Categories);
    }

    [Fact]
    public void SuppressesBeforeDomainOutageWithoutExpandingToOtherTargetAlerts()
    {
        var healthy = CreateAlert("""{"queryResult":{"type":"DCPort","rows":[]}}""");
        var other = CreateAlert("""{"queryResult":{"type":"Heartbeat","rows":[["Failed"]]}}""");
        var rule = CreateDefaultRule();
        var outage = new AlertRule
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            Priority = 10,
            ConditionType = AlertRuleConditionTypes.DomainControllerPercentageInDomain,
            Threshold = 50,
            CategoryName = "Domain Service Outage"
        };
        var inventory = new ComputerInventoryEntry
        {
            SubscriptionId = healthy.SubscriptionId,
            Computer = healthy.TargetName,
            Domain = "contoso.test",
            Site = "site-1",
            Role = "domaincontrollers"
        };

        var result = Evaluator.EvaluateCategorization([healthy, other], [outage, rule], [inventory]);

        Assert.Equal(healthy.Id, Assert.Single(
            Assert.Single(result.Categorization.Categories, category => category.RuleId == rule.Id).Alerts).Id);
        Assert.Equal(other.Id, Assert.Single(
            Assert.Single(result.Categorization.Categories, category => category.RuleId == outage.Id).Alerts).Id);
        Assert.Equal(AlertRuleEvaluationOutcomes.Superseded,
            Assert.Single(result.Decisions, decision =>
                decision.AlertEventId == healthy.Id && decision.RuleId == outage.Id).Outcome);
    }

    internal static AlertRule CreateDefaultRule()
    {
        var seed = Assert.IsType<InsertDataOperation>(
            Assert.Single(new AddHealthyDCPortSuppressionRule().UpOperations));
        Assert.Equal("AlertRules", seed.Table);
        var values = seed.Columns.Select((column, index) => (column, value: seed.Values[0, index]))
            .ToDictionary(item => item.column, item => item.value);
        return JsonSerializer.Deserialize<AlertRule>(JsonSerializer.Serialize(values))
            ?? throw new InvalidOperationException("The migration seed did not deserialize.");
    }

    private static AlertRecord CreateAlert(string rawJson) => new(
        Guid.NewGuid(), DateTimeOffset.Parse("2026-10-09T10:30:00Z"), Guid.NewGuid().ToString(),
        "Domain controller port monitoring", "Sev1", "", "Log", "Fired", "dc-1",
        "rg-test", "sub-test", null, "", "", "", rawJson);
}
