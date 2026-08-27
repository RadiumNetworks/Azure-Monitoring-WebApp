namespace MonitoringApp.Tests;

public sealed class AlertGraphLayoutJsonTests
{
    private static readonly AlertGraphLayoutTestCases Cases =
        TestCaseLoader.Load<AlertGraphLayoutTestCases>("alert-graph-layout.json");
    public static IEnumerable<object[]> LayoutCaseIndexes => Indexes(Cases.Cases.Count);

    [Theory]
    [MemberData(nameof(LayoutCaseIndexes))]
    public void BuildsLayoutsFromJsonTopologies(int caseIndex)
    {
        var testCase = Cases.Cases[caseIndex];
        var layout = AlertGraphLayout.Build(testCase.Roots.Select(Convert).ToArray());
            Assert.Equal(testCase.ExpectedNodes, layout.Nodes.Count);
            Assert.Equal(testCase.ExpectedEdges, layout.Edges.Count);
            Assert.Equal(testCase.ExpectedSubscriptions, layout.Nodes.Count(node => node.Layer == AlertGraphLayer.Subscription));
            Assert.Equal(testCase.ExpectedMiddleNodes, layout.Nodes.Count(node => node.Layer is AlertGraphLayer.ResourceGroup or AlertGraphLayer.AlertName));
            Assert.Equal(testCase.ExpectedTargets, layout.Nodes.Count(node => node.Layer == AlertGraphLayer.Target));

            if (!string.IsNullOrEmpty(testCase.ExpectedLabel))
            {
                Assert.Contains(layout.Nodes, node => node.Label == testCase.ExpectedLabel &&
                    node.Count == testCase.ExpectedLabelCount && node.HistoryCount == testCase.ExpectedLabelHistoryCount);
            }
            Assert.True(layout.Nodes.Select(node => node.X).Distinct().Count() >= testCase.MinimumDistinctX);
            Assert.True(layout.Nodes.Select(node => node.Y).Distinct().Count() >= testCase.MinimumDistinctY);

            if (testCase.AssertNoOverlap)
            {
                AssertNoOverlap(layout.Nodes);
            }
            if (!string.IsNullOrEmpty(testCase.SharedTarget))
            {
                var target = Assert.Single(layout.Nodes, node =>
                    node.Layer == AlertGraphLayer.Target && node.Label.Equals(testCase.SharedTarget, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(testCase.SharedTargetCount, target.Count);
                Assert.Equal(testCase.SharedTargetHistoryCount, target.HistoryCount);
                Assert.Equal(testCase.SharedTargetIncomingEdges,
                    layout.Edges.Count(edge => edge.X2 == target.X && edge.Y2 == target.Y));
            }
            if (testCase.AssertMiddlePositionsDistinct)
            {
                var parents = layout.Nodes.Where(node => node.Layer is AlertGraphLayer.ResourceGroup or AlertGraphLayer.AlertName).ToArray();
                Assert.Equal(parents.Length, parents.Select(node => (node.X, node.Y)).Distinct().Count());
            }
    }

    private static AlertGraphHierarchyNode Convert(GraphHierarchyNodeCase source) => new(
        source.Name, source.Layer, source.Count, source.HistoryCount, source.Children.Select(Convert).ToArray());

    private static void AssertNoOverlap(IReadOnlyList<AlertGraphNode> nodes)
    {
        foreach (var firstNode in nodes)
        {
            foreach (var secondNode in nodes.Where(node => string.CompareOrdinal(node.Id, firstNode.Id) > 0))
            {
                var overlapsHorizontally = Math.Abs(firstNode.X - secondNode.X) < AlertGraphLayout.NodeWidth;
                var overlapsVertically = Math.Abs(firstNode.Y - secondNode.Y) < AlertGraphLayout.NodeHeight;
                Assert.False(overlapsHorizontally && overlapsVertically);
            }
        }
    }

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}