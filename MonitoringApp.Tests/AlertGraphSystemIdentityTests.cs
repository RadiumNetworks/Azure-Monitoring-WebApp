namespace MonitoringApp.Tests;

public sealed class AlertGraphSystemIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AlertGraphLayer.Subscription)]
    [InlineData(AlertGraphLayer.ResourceGroup)]
    [InlineData(AlertGraphLayer.Domain)]
    [InlineData(AlertGraphLayer.Role)]
    public void SameDomainAndTargetAcrossSubscriptionsUseLatestSubscription(AlertGraphLayer rootLayer)
    {
        var maintenance = Record("sub-a", maintenance: true);
        var stale = Record("sub-b", heartbeat: Now.AddMinutes(-6));
        var active = Record("sub-c", "same-alert-id", "Fired", Now.AddMinutes(-1));
        var records = new[]
        {
            maintenance with { ReceivedAt = Now.AddMinutes(-3) },
            stale with { ReceivedAt = Now.AddMinutes(-2) },
            active with { ReceivedAt = Now.AddMinutes(-1) },
            active with
            {
                Id = Guid.NewGuid(),
                SubscriptionId = "sub-d",
                MonitorCondition = "Resolved",
                LastHeartbeatAt = Now,
                ReceivedAt = Now
            }
        };

        var hierarchy = AlertGraphHierarchy.Build(records, rootLayer, AlertGraphLayer.Site, AlertGraphLayer.Target);
        var targets = hierarchy.SelectMany(root => root.Children).SelectMany(middle => middle.Children).ToArray();
        Assert.Single(targets);
        var layout = AlertGraphLayout.Build(hierarchy,
            rootLayer == AlertGraphLayer.Domain ? DnsNamespaceResolver.Resolve(records.Select(record => record.Domain)) : null);
        var nodes = layout.Nodes.Where(node => node.Layer == AlertGraphLayer.Target).ToArray();
        var node = Assert.Single(nodes);
        Assert.Equal("sub-d", node.SubscriptionId);
        Assert.Equal(AlertGraphTargetStatus.Current, AlertGraphTargetStatusResolver.Resolve(node, Now));
        Assert.Equal(0, node.Count);
        Assert.Equal(2, node.HistoryCount);
        Assert.Equal(Now, node.LastHeartbeatAt);
        Assert.False(node.Maintenance);
    }

    [Fact]
    public void SameSystemUnderSeveralAlertNamesSharesOnlyItsOwnEdges()
    {
        var a1 = Record("sub-a", "alert-a", "Fired", Now, maintenance: true) with { AlertName = "Alert A" };
        var a2 = a1 with { Id = Guid.NewGuid(), AlertId = "alert-b", AlertName = "Alert B" };
        var b1 = Record("sub-b", "alert-c", "Fired", Now.AddMinutes(1)) with
        {
            AlertName = "Alert A",
            ReceivedAt = Now.AddMinutes(1)
        };
        var hierarchy = AlertGraphHierarchy.Build([a1, a2, b1],
            AlertGraphLayer.ResourceGroup, AlertGraphLayer.AlertName, AlertGraphLayer.Target);
        var layout = AlertGraphLayout.Build(hierarchy);
        var nodes = layout.Nodes.Where(node => node.Layer == AlertGraphLayer.Target).ToArray();

        var node = Assert.Single(nodes);
        Assert.Equal("sub-b", node.SubscriptionId);
        Assert.Equal(3, node.Count);
        Assert.False(node.Maintenance);
        Assert.Equal(2, layout.Edges.Count(edge => edge.X2 == node.X && edge.Y2 == node.Y));
    }

    [Fact]
    public void DifferentDomainsKeepSystemsWithTheSameTargetSeparate()
    {
        var first = Record("sub-a", "alert-a", "Fired") with { Domain = "a.example" };
        var second = Record("sub-b", "alert-b", "Fired") with { Id = Guid.NewGuid(), Domain = "b.example" };
        var hierarchy = AlertGraphHierarchy.Build([first, second],
            AlertGraphLayer.ResourceGroup, AlertGraphLayer.AlertName, AlertGraphLayer.Target);
        var nodes = AlertGraphLayout.Build(hierarchy).Nodes.Where(node => node.Layer == AlertGraphLayer.Target).ToArray();

        Assert.Equal(["sub-a", "sub-b"], nodes.Select(node => node.SubscriptionId).Order());
        Assert.All(nodes, node => Assert.Equal(1, node.Count));
    }

    [Fact]
    public void DomainAndComputerMatchingIsCaseInsensitive()
    {
        var record = Record("sub-a", "alert-a", "Fired", Now);
        var duplicate = record with
        {
            Id = Guid.NewGuid(),
            AlertId = "alert-b",
            SubscriptionId = "SUB-B",
            Target = "server01",
            Domain = "EXAMPLE.TEST",
            ReceivedAt = Now.AddMinutes(1)
        };
        var hierarchy = AlertGraphHierarchy.Build([record, duplicate],
            AlertGraphLayer.ResourceGroup, AlertGraphLayer.Site, AlertGraphLayer.Target);
        var target = Assert.Single(AlertGraphLayout.Build(hierarchy).Nodes, node => node.Layer == AlertGraphLayer.Target);
        Assert.Equal(2, target.Count);
        Assert.Equal("SUB-B", target.SubscriptionId);
    }

    [Fact]
    public void ServiceHealthAlertFlowsThroughHierarchyAndLayout()
    {
        var inventory = Record("sub-a", heartbeat: Now) with
        {
            HasServiceHealthAlert = true
        };

        var hierarchy = AlertGraphHierarchy.Build(
            [inventory],
            AlertGraphLayer.Domain,
            AlertGraphLayer.Site,
            AlertGraphLayer.Target);
        var node = Assert.Single(
            AlertGraphLayout.Build(hierarchy).Nodes,
            candidate => candidate.Layer == AlertGraphLayer.Target);

        Assert.True(node.HasServiceHealthAlert);
        Assert.Equal(
            AlertGraphTargetStatus.ServiceHealthAlert,
            AlertGraphTargetStatusResolver.Resolve(node, Now));
    }

    private static AlertGraphRecord Record(
        string subscription,
        string alertId = "",
        string condition = "",
        DateTimeOffset? heartbeat = null,
        bool maintenance = false) => new(
            Guid.NewGuid(), Now, alertId, condition, "", "Test", subscription, "shared-rg",
            "SERVER01", "shared-site", "example.test", "DC", heartbeat,
            IsInventoryOnly: string.IsNullOrEmpty(alertId), Maintenance: maintenance);
}
