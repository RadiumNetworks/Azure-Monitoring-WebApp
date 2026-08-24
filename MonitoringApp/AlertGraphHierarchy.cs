namespace MonitoringApp;

public static class AlertGraphHierarchy
{
    public static IReadOnlyList<AlertGraphHierarchyNode> Build(
        IEnumerable<AlertRecord> alerts,
        IEnumerable<AlertRecord> activeAlerts,
        DateTimeOffset receivedSince,
        AlertGraphLayer layer1,
        AlertGraphLayer layer2,
        AlertGraphLayer layer3)
    {
        var recentAlerts = alerts.Where(alert => alert.ReceivedAt >= receivedSince).ToArray();
        var activeAlertIds = activeAlerts.Select(alert => alert.Id).ToHashSet();

        return BuildLevel(recentAlerts, activeAlertIds, [layer1, layer2, layer3], 0);
    }

    public static IReadOnlyList<AlertGraphLayerChoice> ChoicesForLevel(int level) => level switch
    {
        1 => [new(AlertGraphLayer.Subscription, "Subscription")],
        2 =>
        [
            new(AlertGraphLayer.AlertName, "AlertName"),
            new(AlertGraphLayer.ResourceGroup, "Resourcegroup"),
            new(AlertGraphLayer.Site, "Site")
        ],
        3 => [new(AlertGraphLayer.Target, "Target")],
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public static string Label(AlertGraphLayer layer) => layer switch
    {
        AlertGraphLayer.Subscription => "Subscription",
        AlertGraphLayer.AlertName => "AlertName",
        AlertGraphLayer.ResourceGroup => "Resourcegroup",
        AlertGraphLayer.Target => "Target",
        AlertGraphLayer.Site => "Site",
        _ => throw new ArgumentOutOfRangeException(nameof(layer))
    };

    private static IReadOnlyList<AlertGraphHierarchyNode> BuildLevel(
        IReadOnlyList<AlertRecord> alerts,
        HashSet<Guid> activeAlertIds,
        IReadOnlyList<AlertGraphLayer> layers,
        int level)
    {
        var layer = layers[level];

        return alerts
            .GroupBy(alert => ValueFor(alert, layer), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupedAlerts = group.ToArray();
                return new AlertGraphHierarchyNode(
                    group.Key,
                    layer,
                    groupedAlerts.Count(alert => activeAlertIds.Contains(alert.Id)),
                    groupedAlerts.Length,
                    level == layers.Count - 1
                        ? []
                        : BuildLevel(groupedAlerts, activeAlertIds, layers, level + 1));
            })
            .ToArray();
    }

    private static string ValueFor(AlertRecord alert, AlertGraphLayer layer) => layer switch
    {
        AlertGraphLayer.Subscription => alert.SubscriptionId,
        AlertGraphLayer.AlertName => alert.Name,
        AlertGraphLayer.ResourceGroup => alert.ResourceGroup,
        AlertGraphLayer.Target => alert.TargetName,
        AlertGraphLayer.Site => string.IsNullOrWhiteSpace(alert.SiteName) ? "-" : alert.SiteName,
        _ => throw new ArgumentOutOfRangeException(nameof(layer))
    };
}

public sealed record AlertGraphHierarchyNode(
    string Name,
    AlertGraphLayer Layer,
    int Count,
    int HistoryCount,
    IReadOnlyList<AlertGraphHierarchyNode> Children);

public sealed record AlertGraphLayerChoice(AlertGraphLayer Value, string Label);

public enum AlertGraphLayer
{
    Subscription,
    AlertName,
    ResourceGroup,
    Target,
    Site
}