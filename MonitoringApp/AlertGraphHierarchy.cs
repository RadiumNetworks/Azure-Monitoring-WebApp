namespace MonitoringApp;

/// <summary>
/// Builds the configurable three-level hierarchy used by the alert graph. Nodes include active and historical event counts.
/// </summary>
public static class AlertGraphHierarchy
{
    /// <summary>
    /// Groups recent alerts into the requested graph layers and calculates counts from the active-alert set. Alerts older than the cutoff are excluded.
    /// </summary>
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

    /// <summary>
    /// Returns the graph-layer choices allowed at a given hierarchy level. Unsupported level numbers throw an argument-range exception.
    /// </summary>
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

    /// <summary>
    /// Returns the user-facing label for a graph layer. Unknown enum values throw an argument-range exception.
    /// </summary>
    public static string Label(AlertGraphLayer layer) => layer switch
    {
        AlertGraphLayer.Subscription => "Subscription",
        AlertGraphLayer.AlertName => "AlertName",
        AlertGraphLayer.ResourceGroup => "Resourcegroup",
        AlertGraphLayer.Target => "Target",
        AlertGraphLayer.Site => "Site",
        _ => throw new ArgumentOutOfRangeException(nameof(layer))
    };

    /// <summary>
    /// Recursively groups alerts for one hierarchy level and builds its child nodes. Group names are sorted case-insensitively for stable output.
    /// </summary>
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

    /// <summary>
    /// Reads the grouping value for one alert and graph layer. Missing site names are represented by a dash.
    /// </summary>
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

/// <summary>
/// Represents one node in the graph hierarchy before coordinates are calculated. It contains its grouping layer, counts, and child nodes.
/// </summary>
public sealed record AlertGraphHierarchyNode(
    string Name,
    AlertGraphLayer Layer,
    int Count,
    int HistoryCount,
    IReadOnlyList<AlertGraphHierarchyNode> Children);

/// <summary>
/// Pairs a graph-layer enum value with the label shown in the layer selector. It is used to populate graph configuration controls.
/// </summary>
public sealed record AlertGraphLayerChoice(AlertGraphLayer Value, string Label);

/// <summary>
/// Defines the alert fields that can be used as graph hierarchy layers. The selected values control how alert records are grouped.
/// </summary>
public enum AlertGraphLayer
{
    Subscription,
    AlertName,
    ResourceGroup,
    Target,
    Site
}