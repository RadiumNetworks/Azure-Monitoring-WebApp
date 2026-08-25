namespace MonitoringApp.Tests;

public sealed class AlertGraphLayoutTests
{
    [Fact]
    public void BuildsSeparatedTwoDimensionalClustersWithHierarchyEdges()
    {
        var subscriptions = new[]
        {
            new AlertGraphHierarchyNode("sub-a", AlertGraphLayer.Subscription, 3, 8,
            [
                new AlertGraphHierarchyNode("rg-a", AlertGraphLayer.ResourceGroup, 2, 5,
                [
                    new AlertGraphHierarchyNode("target-a1", AlertGraphLayer.Target, 1, 2, []),
                    new AlertGraphHierarchyNode("target-a2", AlertGraphLayer.Target, 1, 3, [])
                ]),
                new AlertGraphHierarchyNode("rg-b", AlertGraphLayer.ResourceGroup, 1, 3,
                [
                    new AlertGraphHierarchyNode("target-b1", AlertGraphLayer.Target, 1, 3, [])
                ])
            ]),
            new AlertGraphHierarchyNode("sub-b", AlertGraphLayer.Subscription, 0, 2,
            [
                new AlertGraphHierarchyNode("rg-c", AlertGraphLayer.ResourceGroup, 0, 2,
                [
                    new AlertGraphHierarchyNode("target-c1", AlertGraphLayer.Target, 0, 1, []),
                    new AlertGraphHierarchyNode("target-c2", AlertGraphLayer.Target, 0, 1, [])
                ])
            ])
        };

        var layout = AlertGraphLayout.Build(subscriptions);

        Assert.Equal(10, layout.Nodes.Count);
        Assert.Equal(8, layout.Edges.Count);
        Assert.Equal(2, layout.Nodes.Count(node => node.Layer == AlertGraphLayer.Subscription));
        Assert.Equal(3, layout.Nodes.Count(node => node.Layer == AlertGraphLayer.ResourceGroup));
        Assert.Equal(5, layout.Nodes.Count(node => node.Layer == AlertGraphLayer.Target));
        Assert.Contains(layout.Nodes, node => node.Label == "sub-a" && node.Count == 3 && node.HistoryCount == 8);
        Assert.True(layout.Nodes.Select(node => node.X).Distinct().Count() > 2);
        Assert.True(layout.Nodes.Select(node => node.Y).Distinct().Count() > 2);

        foreach (var firstNode in layout.Nodes)
        {
            foreach (var secondNode in layout.Nodes.Where(node => string.CompareOrdinal(node.Id, firstNode.Id) > 0))
            {
                var overlapsHorizontally = Math.Abs(firstNode.X - secondNode.X) < AlertGraphLayout.NodeWidth;
                var overlapsVertically = Math.Abs(firstNode.Y - secondNode.Y) < AlertGraphLayout.NodeHeight;
                Assert.False(overlapsHorizontally && overlapsVertically, $"{firstNode.Id} overlaps {secondNode.Id}");
            }
        }
    }

    [Fact]
    public void SharedTargetIsRenderedOnceWithConnectionsFromEachParent()
    {
        var roots = new[]
        {
            new AlertGraphHierarchyNode("sub-a", AlertGraphLayer.Subscription, 2, 3,
            [
                new AlertGraphHierarchyNode("alert-a", AlertGraphLayer.AlertName, 1, 1,
                [
                    new AlertGraphHierarchyNode("server-1", AlertGraphLayer.Target, 1, 1, [])
                ]),
                new AlertGraphHierarchyNode("alert-b", AlertGraphLayer.AlertName, 1, 2,
                [
                    new AlertGraphHierarchyNode("SERVER-1", AlertGraphLayer.Target, 1, 2, [])
                ])
            ])
        };

        var layout = AlertGraphLayout.Build(roots);

        var target = Assert.Single(layout.Nodes, node => node.Layer == AlertGraphLayer.Target);
        Assert.Equal("server-1", target.Label, ignoreCase: true);
        Assert.Equal(2, target.Count);
        Assert.Equal(3, target.HistoryCount);
        Assert.Equal(4, layout.Edges.Count);
        Assert.Equal(2, layout.Edges.Count(edge => edge.X2 == target.X && edge.Y2 == target.Y));

        var parents = layout.Nodes.Where(node => node.Layer == AlertGraphLayer.AlertName).ToArray();
        Assert.Equal(2, parents.Length);
        Assert.NotEqual((parents[0].X, parents[0].Y), (parents[1].X, parents[1].Y));
    }

    [Fact]
    public void SharedTargetIsRenderedOnceAcrossSubscriptions()
    {
        var roots = new[]
        {
            CreateRoot("sub-a", "rg-a", "server-1", 1),
            CreateRoot("sub-b", "rg-b", "SERVER-1", 2)
        };

        var layout = AlertGraphLayout.Build(roots);

        var target = Assert.Single(layout.Nodes, node => node.Layer == AlertGraphLayer.Target);
        Assert.Equal(2, target.Count);
        Assert.Equal(3, target.HistoryCount);
        Assert.Equal(2, layout.Edges.Count(edge => edge.X2 == target.X && edge.Y2 == target.Y));
    }

    private static AlertGraphHierarchyNode CreateRoot(
        string subscription,
        string parent,
        string target,
        int historyCount) => new(
        subscription,
        AlertGraphLayer.Subscription,
        1,
        historyCount,
        [
            new AlertGraphHierarchyNode(parent, AlertGraphLayer.ResourceGroup, 1, historyCount,
            [
                new AlertGraphHierarchyNode(target, AlertGraphLayer.Target, 1, historyCount, [])
            ])
        ]);
}