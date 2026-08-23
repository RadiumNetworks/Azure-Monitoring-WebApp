namespace MonitoringApp;

public static class AlertNavigation
{
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

public sealed record SubscriptionNode(string Name, int Count, int HistoryCount, IReadOnlyList<ResourceGroupNode> ResourceGroups);
public sealed record ResourceGroupNode(string Name, int Count, int HistoryCount, IReadOnlyList<TargetNode> Targets);
public sealed record TargetNode(string Name, int Count, int HistoryCount);
