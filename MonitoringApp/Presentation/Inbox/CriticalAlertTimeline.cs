namespace MonitoringApp;

public sealed record CriticalAlertLifecycle(
    string AlertId,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt);

public sealed record HourlyCriticalAlertCount(
    DateTimeOffset Hour,
    int Count);

/// <summary>
/// Produces hourly snapshots of critical alerts that were active during a 48-hour chart window.
/// </summary>
public static class CriticalAlertTimeline
{
    public static IReadOnlyList<HourlyCriticalAlertCount> GetHourlyCounts(
        IEnumerable<CriticalAlertLifecycle> lifecycles,
        DateTimeOffset firstHour,
        int hourCount,
        DateTimeOffset now) =>
        GetHourlyCounts(lifecycles, firstHour, hourCount, now, []);

    public static IReadOnlyList<HourlyCriticalAlertCount> GetHourlyCounts(
        IEnumerable<CriticalAlertLifecycle> lifecycles,
        DateTimeOffset firstHour,
        int hourCount,
        DateTimeOffset now,
        IEnumerable<string> suppressedActiveAlertIds)
    {
        ArgumentNullException.ThrowIfNull(lifecycles);
        ArgumentNullException.ThrowIfNull(suppressedActiveAlertIds);
        if (hourCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(hourCount));
        }

        var alerts = lifecycles.ToArray();
        // Current suppression must not rewrite earlier hourly snapshots.
        var suppressed = suppressedActiveAlertIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(0, hourCount)
            .Select(offset =>
            {
                var hour = firstHour.AddHours(offset);
                var sampleTime = hour.AddHours(1) < now ? hour.AddHours(1) : now;
                var count = alerts.Count(alert =>
                    alert.StartedAt <= sampleTime &&
                    (alert.ResolvedAt is null || alert.ResolvedAt > sampleTime) &&
                    (sampleTime != now || !suppressed.Contains(alert.AlertId)));
                return new HourlyCriticalAlertCount(hour, count);
            })
            .ToArray();
    }
}
