namespace MonitoringApp;

public static class InventoryMaintenance
{
    public static IReadOnlyList<AlertRecord> FilterAlerts(
        IEnumerable<AlertRecord> alerts,
        IEnumerable<ComputerInventoryEntry> inventory)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(inventory);
        var maintenanceSystems = inventory
            .Where(entry => entry.Maintenance)
            .Select(entry => Key(entry.SubscriptionId, entry.Computer))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return alerts
            .Where(alert => !maintenanceSystems.Contains(Key(alert.SubscriptionId, alert.TargetName)))
            .ToArray();
    }

    private static string Key(string subscriptionId, string computer) =>
        $"{subscriptionId.Trim()}\u001f{computer.Trim()}";
}
