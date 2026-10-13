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

    [Fact]
    public void SharedTargetWithinSubscriptionKeepsNewestHeartbeat()
    {
        var older = new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(15);
        AlertGraphHierarchyNode Target(DateTimeOffset heartbeat) =>
            new("SERVER01", AlertGraphLayer.Target, 3, 0, 0, [], heartbeat, SubscriptionId: "subscription-a");
        AlertGraphHierarchyNode Root(string name, DateTimeOffset heartbeat) =>
            new(name, AlertGraphLayer.ResourceGroup, 1, 0, 0,
                [new("group", AlertGraphLayer.ResourceGroup, 2, 0, 0, [Target(heartbeat)], heartbeat)],
                heartbeat);

        var layout = AlertGraphLayout.Build([Root("rg-a", older), Root("rg-b", newer)]);

        var target = Assert.Single(layout.Nodes, node => node.Layer == AlertGraphLayer.Target);
        Assert.Equal(newer, target.LastHeartbeatAt);
        Assert.Equal("subscription-a", target.SubscriptionId);
    }

    [Fact]
    public void PlacesTargetsInTheDirectionOfTheirLayerTwoParent()
    {
        AlertGraphHierarchyNode Target(string name) =>
            new(name, AlertGraphLayer.Target, 3, 0, 0, []);
        var root = new AlertGraphHierarchyNode(
            "domain.example",
            AlertGraphLayer.Domain,
            1,
            0,
            0,
            [
                new("Site A", AlertGraphLayer.Site, 2, 0, 0, [Target("Z-1"), Target("Z-2")]),
                new("Site B", AlertGraphLayer.Site, 2, 0, 0, [Target("A-1"), Target("A-2")])
            ]);

        var layout = AlertGraphLayout.Build([root]);
        var center = Assert.Single(layout.Nodes, node => node.Level == 1);
        var siteA = Assert.Single(layout.Nodes, node => node.Label == "Site A");
        var siteB = Assert.Single(layout.Nodes, node => node.Label == "Site B");
        var targetsA = layout.Nodes.Where(node => node.Label.StartsWith("Z-", StringComparison.Ordinal)).ToArray();
        var targetsB = layout.Nodes.Where(node => node.Label.StartsWith("A-", StringComparison.Ordinal)).ToArray();

        Assert.All(targetsA, target => Assert.True(SameDirection(center, siteA, target)));
        Assert.All(targetsB, target => Assert.True(SameDirection(center, siteB, target)));
        Assert.False(SegmentsCross(
            siteA,
            targetsA[0],
            siteB,
            targetsB[0]));
        Assert.False(SegmentsCross(
            siteA,
            targetsA[1],
            siteB,
            targetsB[1]));
    }

    [Fact]
    public void LayerTwoNodesNeverOverlapAfterTargetDirectedPlacement()
    {
        AlertGraphHierarchyNode Target(string name) =>
            new(name, AlertGraphLayer.Target, 3, 0, 0, []);
        var shared = Target("shared");
        var root = new AlertGraphHierarchyNode(
            "domain.example",
            AlertGraphLayer.Domain,
            1,
            0,
            0,
            Enumerable.Range(1, 8)
                .Select(index => new AlertGraphHierarchyNode(
                    $"Site {index}",
                    AlertGraphLayer.Site,
                    2,
                    0,
                    0,
                    [shared, Target($"target-{index}")]))
                .ToArray());

        var middleNodes = AlertGraphLayout.Build([root]).Nodes
            .Where(node => node.Level == 2)
            .ToArray();

        Assert.Equal(8, middleNodes.Length);
        AssertNoOverlap(middleNodes);
    }

    [Fact]
    public void SegmentsDomainsByNearestKnownDnsNamespace()
    {
        var domains = new[]
        {
            "cool.ice.corp.com",
            "ice.corp.com",
            "cooltest.icetest.corp.com",
            "icetest.corp.com"
        };
        var namespaces = DnsNamespaceResolver.Resolve(domains);
        var roots = domains.Select(domain => new AlertGraphHierarchyNode(
            domain,
            AlertGraphLayer.Domain,
            1,
            0,
            0,
            [new("site", AlertGraphLayer.Site, 2, 0, 0,
                [new($"target-{domain}", AlertGraphLayer.Target, 3, 0, 0, [])])])).ToArray();

        var layout = AlertGraphLayout.Build(roots, namespaces);

        Assert.Equal("ice.corp.com", namespaces["cool.ice.corp.com"]);
        Assert.Equal("icetest.corp.com", namespaces["cooltest.icetest.corp.com"]);
        Assert.Equal(["ice.corp.com", "icetest.corp.com"], layout.Segments.Select(segment => segment.Label));
        var first = layout.Segments[0];
        var second = layout.Segments[1];
        Assert.True(
            first.X + first.Width <= second.X || second.X + second.Width <= first.X ||
            first.Y + first.Height <= second.Y || second.Y + second.Height <= first.Y);
    }

    private static AlertGraphHierarchyNode Convert(GraphHierarchyNodeCase source, int level = 1) => new(
        source.Name,
        source.Layer,
        level,
        source.Count,
        source.HistoryCount,
        source.Children.Select(child => Convert(child, level + 1)).ToArray());

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

    private static bool SameDirection(
        AlertGraphNode center,
        AlertGraphNode parent,
        AlertGraphNode target) =>
        (parent.X - center.X) * (target.X - center.X) +
        (parent.Y - center.Y) * (target.Y - center.Y) > 0;

    private static bool SegmentsCross(
        AlertGraphNode firstStart,
        AlertGraphNode firstEnd,
        AlertGraphNode secondStart,
        AlertGraphNode secondEnd)
    {
        static double Orientation(AlertGraphNode a, AlertGraphNode b, AlertGraphNode c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        return Math.Sign(Orientation(firstStart, firstEnd, secondStart)) !=
                Math.Sign(Orientation(firstStart, firstEnd, secondEnd)) &&
            Math.Sign(Orientation(secondStart, secondEnd, firstStart)) !=
                Math.Sign(Orientation(secondStart, secondEnd, firstEnd));
    }
}