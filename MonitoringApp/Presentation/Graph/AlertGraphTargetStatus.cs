namespace MonitoringApp;

public enum AlertGraphTargetStatus
{
    None,
    OpenAlert,
    ServiceHealthAlert,
    Current,
    Stale,
    Maintenance
}

public static class AlertGraphTargetStatusResolver
{
    public static readonly TimeSpan CurrentThreshold =
        TimeSpan.FromMinutes(InventoryHeartbeatFreshnessRule.DefaultMinutes);

    public static AlertGraphTargetStatus Resolve(
        AlertGraphNode node,
        DateTimeOffset currentTimeUtc,
        TimeSpan? currentHeartbeatAge = null)
    {
        var threshold = currentHeartbeatAge ?? CurrentThreshold;
        if (threshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(currentHeartbeatAge));
        }

        if (node.Level != 3 || node.Layer != AlertGraphLayer.Target)
        {
            return AlertGraphTargetStatus.None;
        }

        if (node.Maintenance)
        {
            return AlertGraphTargetStatus.Maintenance;
        }

        if (node.Count > 0)
        {
            return AlertGraphTargetStatus.OpenAlert;
        }

        if (node.HasServiceHealthAlert)
        {
            return AlertGraphTargetStatus.ServiceHealthAlert;
        }

        return InventoryHeartbeatFreshnessRule.IsCurrent(node.LastHeartbeatAt, currentTimeUtc, threshold)
                ? AlertGraphTargetStatus.Current
                : AlertGraphTargetStatus.Stale;
    }
}