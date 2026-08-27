namespace MonitoringApp.Tests;

public sealed class AlertGraphHierarchyTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LayerTwoCanGroupByResourceGroupOrAlertName()
    {
        var alerts = new[]
        {
            CreateAlert("alert-1", "Disk space", "rg-app", "server-1"),
            CreateAlert("alert-2", "CPU load", "rg-app", "server-2"),
            CreateAlert("alert-3", "Disk space", "rg-data", "server-3")
        };
        var activeAlerts = AlertLifecycle.GetActiveAlerts(alerts);

        var byResourceGroup = AlertGraphHierarchy.Build(
            alerts,
            activeAlerts,
            BaseTime.AddDays(-7),
            AlertGraphLayer.Subscription,
            AlertGraphLayer.ResourceGroup,
            AlertGraphLayer.Target);
        var byAlertName = AlertGraphHierarchy.Build(
            alerts,
            activeAlerts,
            BaseTime.AddDays(-7),
            AlertGraphLayer.Subscription,
            AlertGraphLayer.AlertName,
            AlertGraphLayer.Target);

        Assert.Equal(["rg-app", "rg-data"], Assert.Single(byResourceGroup).Children.Select(node => node.Name));
        Assert.Equal(["CPU load", "Disk space"], Assert.Single(byAlertName).Children.Select(node => node.Name));
        Assert.Equal(3, Assert.Single(byResourceGroup).Count);
        Assert.Equal(3, Assert.Single(byAlertName).Count);
        Assert.Equal(3, Assert.Single(byResourceGroup).Children.Sum(node => node.Children.Count));
        Assert.Equal(3, Assert.Single(byAlertName).Children.Sum(node => node.Children.Count));
    }

    [Fact]
    public void ExposesOnlyCurrentlySupportedChoicesForEachLayer()
    {
        Assert.Equal([AlertGraphLayer.Subscription], AlertGraphHierarchy.ChoicesForLevel(1).Select(choice => choice.Value));
        Assert.Equal(
            [AlertGraphLayer.AlertName, AlertGraphLayer.ResourceGroup, AlertGraphLayer.Site],
            AlertGraphHierarchy.ChoicesForLevel(2).Select(choice => choice.Value));
        Assert.Equal([AlertGraphLayer.Target], AlertGraphHierarchy.ChoicesForLevel(3).Select(choice => choice.Value));
    }

    [Fact]
    public void LayerTwoCanGroupBySiteDimensionWithTargetChildren()
    {
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var alerts = new[]
        {
            CreateAlert("alert-1", "Disk space", "rg-app", "server-1", "Site", "Berlin"),
            CreateAlert("alert-2", "CPU load", "rg-app", "server-2", "SourceDSASite", "Munich"),
            CreateAlert("alert-3", "Disk space", "rg-app", "server-3")
        }.Select(alert => alert with { DisplayIdentity = presenter.ResolveIdentity(alert) }).ToArray();

        var hierarchy = AlertGraphHierarchy.Build(
            alerts,
            AlertLifecycle.GetActiveAlerts(alerts),
            BaseTime.AddDays(-7),
            AlertGraphLayer.Subscription,
            AlertGraphLayer.Site,
            AlertGraphLayer.Target);

        var sites = Assert.Single(hierarchy).Children;
        Assert.Equal(["-", "Berlin", "Munich"], sites.Select(node => node.Name));
        Assert.Equal(3, sites.Sum(node => node.Children.Count));
    }

    private static AlertRecord CreateAlert(
        string alertId,
        string name,
        string resourceGroup,
        string target,
        string? dimensionName = null,
        string? dimensionValue = null) => new(
        Guid.NewGuid(),
        BaseTime,
        alertId,
        name,
        "Sev2",
        string.Empty,
        "Log",
        "Fired",
        target,
        resourceGroup,
        "sub-a",
        BaseTime,
        string.Empty,
        string.Empty,
        string.Empty,
                dimensionName is null
                        ? "{}"
                        : $$"""
                                {
                                    "data": {
                                        "alertContext": {
                                            "condition": {
                                                "allOf": [
                                                    {
                                                        "dimensions": [
                                                            { "name": "{{dimensionName}}", "value": "{{dimensionValue}}" }
                                                        ]
                                                    }
                                                ]
                                            }
                                        }
                                    }
                                }
                                """);
}