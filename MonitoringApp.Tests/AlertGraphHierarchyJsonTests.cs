using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class AlertGraphHierarchyJsonTests
{
    private static readonly AlertGraphHierarchyTestCases Cases =
        TestCaseLoader.Load<AlertGraphHierarchyTestCases>("alert-graph-hierarchy.json");
    private static readonly AlertHistoryOptions HistoryOptions = new();
    private static readonly AlertGraphOptions GraphOptions = CreateGraphOptions();
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
    public static IEnumerable<object[]> GroupingCaseIndexes => Indexes(Cases.Groupings.Count);
    public static IEnumerable<object[]> ChoiceCaseIndexes => Indexes(Cases.Choices.Count);
    public static IEnumerable<object[]> RecordGroupingCaseIndexes => Indexes(Cases.RecordGroupings.Count);

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
        var choices = GraphOptions.ChoicesForLevel(testCase.Level);
        Assert.Equal(testCase.ExpectedValues, choices.Select(choice => choice.Value));
        Assert.Equal(testCase.ExpectedLabels, choices.Select(choice => choice.Label));
    }

    [Theory]
    [MemberData(nameof(RecordGroupingCaseIndexes))]
    public void GroupsParsedHistoryUsingJsonCases(int caseIndex)
    {
        var testCase = Cases.RecordGroupings[caseIndex];
        var source = testCase.Alert;
        var alert = new AlertGraphRecord(
            Guid.NewGuid(), source.ReceivedAt, source.AlertId, source.MonitorCondition, source.Category,
            source.AlertName, source.SubscriptionId, source.ResourceGroup, source.Target, source.Site,
            source.Domain, source.Role);

        var hierarchy = AlertGraphHierarchy.Build(
            [alert], testCase.Layers[0], testCase.Layers[1], testCase.Layers[2]);

        var root = Assert.Single(hierarchy);
        var middle = Assert.Single(root.Children);
        var leaf = Assert.Single(middle.Children);
        Assert.Equal(testCase.ExpectedNames, new[] { root.Name, middle.Name, leaf.Name });
        Assert.Equal(testCase.ExpectedRootHistoryCount, root.HistoryCount);
        Assert.Equal(testCase.ExpectedRootCount, root.Count);
    }

    private static void AssertGrouping(GraphGroupingCase testCase, AlertRecord[] alerts)
    {
        var hierarchy = AlertGraphHierarchy.Build(
            alerts, AlertLifecycle.GetActiveAlerts(alerts), HistoryOptions.GetCutoff(Cases.BaseTime),
            testCase.Layers[0], testCase.Layers[1], testCase.Layers[2]);
        var root = Assert.Single(hierarchy);
        Assert.Equal(1, root.Level);
        Assert.All(root.Children, node => Assert.Equal(2, node.Level));
        Assert.All(root.Children.SelectMany(node => node.Children), node => Assert.Equal(3, node.Level));
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

    private static AlertGraphOptions CreateGraphOptions()
    {
        AlertGraphLayerChoice[] Choices(int level)
        {
            var configured = Cases.Choices.Single(choice => choice.Level == level);
            return configured.ExpectedValues
                .Zip(configured.ExpectedLabels, (value, label) => new AlertGraphLayerChoice(value, label))
                .ToArray();
        }

        return new AlertGraphOptions
        {
            Layer1 = Choices(1),
            Layer2 = Choices(2),
            Layer3 = Choices(3),
            DefaultLayer1 = AlertGraphLayer.Subscription,
            DefaultLayer2 = AlertGraphLayer.ResourceGroup,
            DefaultLayer3 = AlertGraphLayer.Target
        };
    }
}