namespace MonitoringApp;

public enum AlertGraphTargetStatus
{
    None,
    OpenAlert,
    Current,
    Stale
}

public static class AlertGraphTargetStatusResolver
{
    public static readonly TimeSpan CurrentThreshold = TimeSpan.FromMinutes(20);

    public static AlertGraphTargetStatus Resolve(AlertGraphNode node, DateTimeOffset currentTimeUtc)
    {
        if (node.Level != 3 || node.Layer != AlertGraphLayer.Target)
        {
            return AlertGraphTargetStatus.None;
        }

        if (node.Count > 0)
        {
            return AlertGraphTargetStatus.OpenAlert;
        }

        return node.LastHeartbeatAt is not null &&
            currentTimeUtc - node.LastHeartbeatAt.Value <= CurrentThreshold
                ? AlertGraphTargetStatus.Current
                : AlertGraphTargetStatus.Stale;
    }
}