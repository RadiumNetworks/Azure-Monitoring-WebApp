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
            .Select(entry => SubscriptionComputerKey.Create(entry.SubscriptionId, entry.Computer))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return alerts
            .Where(alert => !maintenanceSystems.Contains(SubscriptionComputerKey.Create(alert.SubscriptionId, alert.TargetName)))
            .ToArray();
    }
}
