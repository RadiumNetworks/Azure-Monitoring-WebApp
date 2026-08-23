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
}