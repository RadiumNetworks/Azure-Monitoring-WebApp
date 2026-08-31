namespace MonitoringApp;

/// <summary>
/// Builds the configurable three-level hierarchy used by the alert graph. Nodes include active and historical event counts.
/// </summary>
public static class AlertGraphHierarchy
{
    /// <summary>
    /// Groups all parsed alert history using inventory-backed values and calculates active counts from the latest event per alert ID.
    /// </summary>
    public static IReadOnlyList<AlertGraphHierarchyNode> Build(
        IEnumerable<AlertGraphRecord> alerts,
        AlertGraphLayer layer1,
        AlertGraphLayer layer2,
        AlertGraphLayer layer3)
    {
        var snapshot = alerts.ToArray();
        var activeAlertIds = snapshot
            .Where(alert => !string.IsNullOrWhiteSpace(alert.AlertId))
            .GroupBy(alert => alert.AlertId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(alert => alert.ReceivedAt).First())
            .Concat(snapshot.Where(alert => string.IsNullOrWhiteSpace(alert.AlertId)))
            .Where(alert => alert.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase) &&
                !System.Text.RegularExpressions.Regex.IsMatch(
                    alert.Comments,
                    @"\bResolved\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .Select(alert => alert.Id)
            .ToHashSet();

        return BuildGraphLevel(snapshot, activeAlertIds, [layer1, layer2, layer3], 0);
    }

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
                    level + 1,
                    groupedAlerts.Count(alert => activeAlertIds.Contains(alert.Id)),
                    groupedAlerts.Length,
                    level == layers.Count - 1
                        ? []
                        : BuildLevel(groupedAlerts, activeAlertIds, layers, level + 1));
            })
            .ToArray();
    }

    private static IReadOnlyList<AlertGraphHierarchyNode> BuildGraphLevel(
        IReadOnlyList<AlertGraphRecord> alerts,
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
                    level + 1,
                    groupedAlerts.Count(alert => activeAlertIds.Contains(alert.Id)),
                    groupedAlerts.Length,
                    level == layers.Count - 1
                        ? []
                        : BuildGraphLevel(groupedAlerts, activeAlertIds, layers, level + 1));
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

    private static string ValueFor(AlertGraphRecord alert, AlertGraphLayer layer)
    {
        var value = layer switch
        {
            AlertGraphLayer.Subscription => alert.SubscriptionId,
            AlertGraphLayer.AlertName => alert.AlertName,
            AlertGraphLayer.ResourceGroup => alert.ResourceGroup,
            AlertGraphLayer.Target => alert.Target,
            AlertGraphLayer.Site => alert.Site,
            AlertGraphLayer.Domain => alert.Domain,
            AlertGraphLayer.Role => alert.Role,
            _ => throw new ArgumentOutOfRangeException(nameof(layer))
        };
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}

/// <summary>
/// Contains graph fields read from ParsedAlerts and its linked, operator-maintained inventory record.
/// </summary>
public sealed record AlertGraphRecord(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string AlertId,
    string MonitorCondition,
    string Comments,
    string AlertName,
    string SubscriptionId,
    string ResourceGroup,
    string Target,
    string Site,
    string Domain,
    string Role);

/// <summary>
/// Represents one node in the graph hierarchy before coordinates are calculated. It contains its grouping layer, counts, and child nodes.
/// </summary>
public sealed record AlertGraphHierarchyNode(
    string Name,
    AlertGraphLayer Layer,
    int Level,
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
    Site,
    Domain,
    Role
}