using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class AlertGraphHierarchyJsonTests
{
    private static readonly AlertGraphHierarchyTestCases Cases =
        TestCaseLoader.Load<AlertGraphHierarchyTestCases>("alert-graph-hierarchy.json");
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
    public static IEnumerable<object[]> GroupingCaseIndexes => Indexes(Cases.Groupings.Count);
    public static IEnumerable<object[]> ChoiceCaseIndexes => Indexes(Cases.Choices.Count);

    [Theory]
    [MemberData(nameof(GroupingCaseIndexes))]
    public void GroupsAlertsUsingJsonLayerCases(int caseIndex)
    {
        var alerts = Cases.GroupingAlerts.Select(CreateAlert).ToArray();
        AssertGrouping(Cases.Groupings[caseIndex], alerts);
    }

    [Fact]
    public void GroupsDescriptorResolvedSitesUsingJsonCase()
    {
        var alerts = Cases.SiteAlerts
            .Select(CreateAlert)
            .Select(alert => alert with { DisplayIdentity = Presenter.ResolveIdentity(alert) })
            .ToArray();
        AssertGrouping(Cases.SiteGrouping, alerts);
    }

    [Theory]
    [MemberData(nameof(ChoiceCaseIndexes))]
    public void ExposesOnlyJsonConfiguredExpectedChoicesForEachLayer(int caseIndex)
    {
        var testCase = Cases.Choices[caseIndex];
        var choices = AlertGraphHierarchy.ChoicesForLevel(testCase.Level);
        Assert.Equal(testCase.ExpectedValues, choices.Select(choice => choice.Value));
        Assert.Equal(testCase.ExpectedLabels, choices.Select(choice => choice.Label));
    }

    private static void AssertGrouping(GraphGroupingCase testCase, AlertRecord[] alerts)
    {
        var hierarchy = AlertGraphHierarchy.Build(
            alerts, AlertLifecycle.GetActiveAlerts(alerts), Cases.BaseTime.AddDays(-7),
            testCase.Layers[0], testCase.Layers[1], testCase.Layers[2]);
        var root = Assert.Single(hierarchy);
        Assert.Equal(testCase.ExpectedMiddleNames, root.Children.Select(node => node.Name));
        Assert.Equal(testCase.ExpectedRootCount, root.Count);
        Assert.Equal(testCase.ExpectedLeafCount, root.Children.Sum(node => node.Children.Count));
    }

    private static AlertRecord CreateAlert(GraphAlertCase source)
    {
        var rawJson = source.DimensionName is null
            ? "{}"
            : new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["alertContext"] = new JsonObject
                    {
                        ["condition"] = new JsonObject
                        {
                            ["allOf"] = new JsonArray(new JsonObject
                            {
                                ["dimensions"] = new JsonArray(new JsonObject
                                {
                                    ["name"] = source.DimensionName,
                                    ["value"] = source.DimensionValue
                                })
                            })
                        }
                    }
                }
            }.ToJsonString();
        return new AlertRecord(
            Guid.NewGuid(), Cases.BaseTime, source.AlertId, source.Name, string.Empty, string.Empty,
            string.Empty, source.Condition, source.Target, source.ResourceGroup, source.SubscriptionId,
            Cases.BaseTime, string.Empty, string.Empty, string.Empty, rawJson);
    }

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}