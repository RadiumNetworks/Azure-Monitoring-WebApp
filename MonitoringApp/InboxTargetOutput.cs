namespace MonitoringApp;

public static class InboxTargetOutput
{
    public static IReadOnlyList<InboxTargetOutputCell> Build(
        IEnumerable<AlertRecord> visibleAlerts,
        IEnumerable<AlertRecord> activeAlerts,
        IEnumerable<ComputerInventoryEntry> inventory,
        DateTimeOffset currentTimeUtc,
        TimeSpan currentHeartbeatAge,
        Func<ComputerInventoryEntry, bool>? matchesInventory = null,
        IReadOnlyList<AlertCategoryGroup>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(visibleAlerts);
        ArgumentNullException.ThrowIfNull(activeAlerts);
        ArgumentNullException.ThrowIfNull(inventory);
        if (currentHeartbeatAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(currentHeartbeatAge));
        }

        var visibleAlertSnapshot = visibleAlerts.ToArray();
        var activeAlertSnapshot = activeAlerts.ToArray();
        var inventorySnapshot = inventory.ToArray();
        var inventoryBySubscriptionTarget = inventorySnapshot
            .GroupBy(
                entry => SubscriptionTargetKey(entry.SubscriptionId, entry.Computer),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.LastHeartbeatAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var inventorySystems = inventorySnapshot
            .GroupBy(entry => SystemKey(entry.Domain, entry.Computer), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.LastHeartbeatAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var categoryByAlert = (categories ?? [])
            .SelectMany(category => category.Alerts.Select(alert => (alert.Id, Category: category)))
            .ToDictionary(item => item.Id, item => item.Category);

        string KeyFor(AlertRecord alert)
        {
            inventoryBySubscriptionTarget.TryGetValue(
                SubscriptionTargetKey(alert.SubscriptionId, alert.TargetName),
                out var entry);
            return SystemKey(entry?.Domain, alert.TargetName);
        }

        var allRelevantAlerts = visibleAlertSnapshot.Concat(activeAlertSnapshot)
            .GroupBy(alert => alert.Id)
            .Select(group => group.First())
            .ToArray();
        var latestSubscriptionBySystem = inventorySnapshot
            .Select(entry => new SubscriptionUse(
                SystemKey(entry.Domain, entry.Computer),
                entry.SubscriptionId,
                entry.LastHeartbeatAt ?? DateTimeOffset.MinValue))
            .Concat(allRelevantAlerts.Select(alert => new SubscriptionUse(
                KeyFor(alert),
                alert.SubscriptionId,
                alert.ReceivedAt)))
            .GroupBy(use => use.SystemKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(use => use.UsedAt).First().SubscriptionId,
                StringComparer.OrdinalIgnoreCase);
        var activeSystems = activeAlertSnapshot
            .Select(KeyFor)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayedSystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cells = InventoryMaintenance.FilterAlerts(visibleAlertSnapshot, inventorySnapshot)
            .GroupBy(
                KeyFor,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var alert = group.OrderByDescending(candidate => candidate.ReceivedAt).First();
                var key = group.Key;
                displayedSystems.Add(key);
                var missingHeartbeat = inventorySystems.TryGetValue(key, out var entry) &&
                    !activeSystems.Contains(key) &&
                    !InventoryHeartbeatFreshnessRule.IsCurrent(entry.LastHeartbeatAt, currentTimeUtc, currentHeartbeatAge);
                var groupCategories = group
                    .Where(candidate => categoryByAlert.ContainsKey(candidate.Id))
                    .Select(candidate => categoryByAlert[candidate.Id])
                    .ToArray();
                var suppressedCount = group.Count(candidate =>
                    categoryByAlert.TryGetValue(candidate.Id, out var category) &&
                    category.AnalysisClassification == AlertRuleAnalysisClassifications.Suppressed);
                return new InboxTargetOutputCell(
                    latestSubscriptionBySystem[key],
                    string.IsNullOrWhiteSpace(alert.TargetName) ? "Unknown target" : alert.TargetName,
                    Site(alert.SiteName),
                    group.Count(),
                    missingHeartbeat,
                    Domain(entry?.Domain),
                    group.Count() - suppressedCount,
                    suppressedCount == group.Count(),
                    groupCategories.Any(category =>
                        category.AnalysisClassification == AlertRuleAnalysisClassifications.SystemOutage),
                    groupCategories.Any(category =>
                        category.AnalysisClassification == AlertRuleAnalysisClassifications.SystemOutage &&
                        category.IsCritical));
            })
            .ToList();

        foreach (var (key, entry) in inventorySystems)
        {
            var missingHeartbeat =
                !InventoryHeartbeatFreshnessRule.IsCurrent(
                    entry.LastHeartbeatAt,
                    currentTimeUtc,
                    currentHeartbeatAge);
            if (!entry.Maintenance &&
                !displayedSystems.Contains(key) &&
                !activeSystems.Contains(key) &&
                (matchesInventory is null || matchesInventory(entry)) &&
                (entry.HasServiceHealthAlert || missingHeartbeat))
            {
                cells.Add(new InboxTargetOutputCell(
                    latestSubscriptionBySystem[key],
                    entry.Computer,
                    Site(entry.Site),
                    0,
                    missingHeartbeat,
                    Domain(entry.Domain),
                    HasServiceHealthAlert: entry.HasServiceHealthAlert));
            }
        }

        return cells
            .OrderByDescending(cell => !cell.MissingHeartbeat)
            .ThenByDescending(cell => cell.Count)
            .ThenBy(cell => cell.Site, StringComparer.OrdinalIgnoreCase)
            .ThenBy(cell => cell.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(cell => cell.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string SubscriptionTargetKey(string subscriptionId, string computer) =>
        $"{subscriptionId.Trim()}\u001f{computer.Trim()}";

    private static string SystemKey(string? domain, string computer) =>
        $"{Domain(domain)}\u001f{computer.Trim()}";

    private static string Site(string? site) =>
        string.IsNullOrWhiteSpace(site) ? "Unknown site" : site.Trim();

    private static string Domain(string? domain) =>
        string.IsNullOrWhiteSpace(domain) ? "Unknown domain" : domain.Trim();

    private sealed record SubscriptionUse(string SystemKey, string SubscriptionId, DateTimeOffset UsedAt);
}

public enum InboxTargetColor
{
    MissingHeartbeat,
    Purple,
    Green,
    Yellow,
    Orange,
    Red,
    Gray
}

public sealed record InboxTargetOutputCell(
    string SubscriptionId,
    string Target,
    string Site,
    int Count,
    bool MissingHeartbeat,
    string Domain = "Unknown domain",
    int NonSuppressedCount = 0,
    bool SuppressedOnly = false,
    bool IsOutage = false,
    bool IsCriticalOutage = false,
    bool HasServiceHealthAlert = false)
{
    private static readonly InboxTargetOutputDisplayOptions DefaultDisplay = new();

    public InboxTargetColor Color => DefaultDisplay.Color(this);
    public string CssClass => DefaultDisplay.CssClass(this);
    public string Style => DefaultDisplay.Style(this);
}
