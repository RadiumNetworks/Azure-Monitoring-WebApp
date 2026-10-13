namespace MonitoringApp;

public static class InventoryHeartbeatFreshnessRule
{
    public const int DefaultMinutes = 5;

    public static bool IsCurrent(
        DateTimeOffset? lastHeartbeatAt,
        DateTimeOffset currentTimeUtc,
        TimeSpan currentHeartbeatAge)
    {
        if (currentHeartbeatAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(currentHeartbeatAge));
        }

        return lastHeartbeatAt.HasValue &&
            currentTimeUtc - lastHeartbeatAt.Value <= currentHeartbeatAge;
    }

    public static TimeSpan Resolve(IEnumerable<AlertRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var candidates = rules
            .Where(rule => rule.RuleType == AlertRuleTypes.InventoryHeartbeatFreshness)
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new InvalidOperationException("Only one global inventory heartbeat freshness rule is allowed.");
        }

        var rule = candidates.SingleOrDefault();
        if (rule is null || !rule.Enabled)
        {
            return TimeSpan.FromMinutes(DefaultMinutes);
        }

        if (rule.Threshold < 1)
        {
            throw new InvalidOperationException("The inventory heartbeat freshness limit must be at least 1 minute.");
        }

        return TimeSpan.FromMinutes(rule.Threshold);
    }
}
