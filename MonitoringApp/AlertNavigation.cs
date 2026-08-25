namespace MonitoringApp;

/// <summary>
/// Builds the subscription, resource-group, and target tree shown beside the alert table. Each level includes active and historical counts.
/// </summary>
public static class AlertNavigation
{
    /// <summary>
    /// Groups recent alerts into a sorted navigation hierarchy and marks which records are active. Alerts older than the supplied cutoff are excluded.
    /// </summary>
    public static IReadOnlyList<SubscriptionNode> Build(
        IEnumerable<AlertRecord> alerts,
        IEnumerable<AlertRecord> activeAlerts,
        DateTimeOffset receivedSince)
    {
        var activeAlertIds = activeAlerts.Select(alert => alert.Id).ToHashSet();

        return alerts
            .Where(alert => alert.ReceivedAt >= receivedSince)
            .GroupBy(alert => alert.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(subscription => new SubscriptionNode(
                subscription.Key,
                subscription.Count(alert => activeAlertIds.Contains(alert.Id)),
                subscription.Count(),
                subscription
                    .GroupBy(alert => alert.ResourceGroup, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(resourceGroup => new ResourceGroupNode(
                        resourceGroup.Key,
                        resourceGroup.Count(alert => activeAlertIds.Contains(alert.Id)),
                        resourceGroup.Count(),
                        resourceGroup
                            .GroupBy(alert => alert.TargetName, StringComparer.OrdinalIgnoreCase)
                            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(target => new TargetNode(
                                target.Key,
                                target.Count(alert => activeAlertIds.Contains(alert.Id)),
                                target.Count()))
                            .ToArray()))
                    .ToArray()))
            .ToArray();
    }
}

/// <summary>
/// Represents one subscription and its resource-group children in the alert navigation tree. Count is active alerts, while HistoryCount includes all recent events.
/// </summary>
public sealed record SubscriptionNode(string Name, int Count, int HistoryCount, IReadOnlyList<ResourceGroupNode> ResourceGroups);

/// <summary>
/// Represents one resource group and its target children in the alert navigation tree. It carries both active and historical counts.
/// </summary>
public sealed record ResourceGroupNode(string Name, int Count, int HistoryCount, IReadOnlyList<TargetNode> Targets);

/// <summary>
/// Represents a leaf target in the alert navigation tree. It reports active and historical alert counts for that target.
/// </summary>
public sealed record TargetNode(string Name, int Count, int HistoryCount);
