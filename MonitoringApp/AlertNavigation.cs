namespace MonitoringApp;

/// <summary>
/// Builds the inventory-backed domain, role, and target tree shown beside the alert table. Each level includes active and historical alert counts.
/// </summary>
public static class AlertNavigation
{
    private const string UnknownDomain = "Unknown domain";
    private const string UnknownRole = "Unknown role";

    /// <summary>
    /// Builds a sorted hierarchy exclusively from known inventory systems. Unmatched alerts contribute no navigation nodes.
    /// </summary>
    public static IReadOnlyList<DomainNode> Build(
        IEnumerable<AlertRecord> alerts,
        IEnumerable<AlertRecord> activeAlerts,
        IEnumerable<ComputerInventoryEntry> inventory,
        DateTimeOffset receivedSince)
    {
        var recentAlerts = alerts.Where(alert => alert.ReceivedAt >= receivedSince).ToArray();
        var activeAlertIds = activeAlerts.Select(alert => alert.Id).ToHashSet();
        var alertsByTarget = recentAlerts
            .GroupBy(alert => SubscriptionTargetKey(alert.SubscriptionId, alert.TargetName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var inventoryTargets = inventory
            .GroupBy(
                entry => SystemKey(entry.Domain, entry.Computer),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToArray();
                var latest = entries
                    .OrderByDescending(entry =>
                        alertsByTarget
                            .GetValueOrDefault(SubscriptionTargetKey(entry.SubscriptionId, entry.Computer))?
                            .Max(alert => alert.ReceivedAt) ??
                        entry.LastHeartbeatAt ??
                        DateTimeOffset.MinValue)
                    .First();
                var systemAlerts = entries
                    .SelectMany(entry =>
                        alertsByTarget.GetValueOrDefault(
                            SubscriptionTargetKey(entry.SubscriptionId, entry.Computer)) ?? [])
                    .DistinctBy(alert => alert.Id)
                    .ToArray();
                return new NavigationTarget(
                    latest.Computer,
                    ValueOrFallback(latest.Domain, UnknownDomain),
                    ValueOrFallback(latest.Role, UnknownRole),
                    ValueOrFallback(latest.Site, null),
                    systemAlerts);
            })
            .ToArray();
        return inventoryTargets
            .GroupBy(target => target.Domain, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(domain => new DomainNode(
                domain.Key,
                domain.Sum(target => target.Alerts.Count(alert => activeAlertIds.Contains(alert.Id))),
                domain.Sum(target => target.Alerts.Count),
                domain
                    .GroupBy(target => target.Role, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(role => new RoleNode(
                        role.Key,
                        role.Sum(target => target.Alerts.Count(alert => activeAlertIds.Contains(alert.Id))),
                        role.Sum(target => target.Alerts.Count),
                        role
                            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(target => new TargetNode(
                                target.Name,
                                target.Site,
                                target.Alerts.Count(alert => activeAlertIds.Contains(alert.Id)),
                                target.Alerts.Count))
                            .ToArray()))
                    .ToArray()))
            .ToArray();
    }

    private static string SubscriptionTargetKey(string subscriptionId, string computer) =>
        $"{subscriptionId.Trim()}\u001f{computer.Trim()}";

    private static string SystemKey(string? domain, string computer) =>
        $"{ValueOrFallback(domain, UnknownDomain)}\u001f{computer.Trim()}";

    private static string ValueOrFallback(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? string.IsNullOrWhiteSpace(fallback) ? "-" : fallback.Trim()
            : value.Trim();

    private sealed record NavigationTarget(
        string Name,
        string Domain,
        string Role,
        string Site,
        IReadOnlyList<AlertRecord> Alerts);
}

public sealed record DomainNode(string Name, int Count, int HistoryCount, IReadOnlyList<RoleNode> Roles);

public sealed record RoleNode(string Name, int Count, int HistoryCount, IReadOnlyList<TargetNode> Targets);

public sealed record TargetNode(string Name, string Site, int Count, int HistoryCount)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Site) || Site == "-" ? Name : $"{Name} ({Site})";
}
