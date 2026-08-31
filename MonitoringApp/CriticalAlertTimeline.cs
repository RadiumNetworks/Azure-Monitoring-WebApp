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
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lifecycles);
        if (hourCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(hourCount));
        }

        var alerts = lifecycles.ToArray();
        return Enumerable.Range(0, hourCount)
            .Select(offset =>
            {
                var hour = firstHour.AddHours(offset);
                var sampleTime = hour.AddHours(1) < now ? hour.AddHours(1) : now;
                var count = alerts.Count(alert =>
                    alert.StartedAt <= sampleTime &&
                    (alert.ResolvedAt is null || alert.ResolvedAt > sampleTime));
                return new HourlyCriticalAlertCount(hour, count);
            })
            .ToArray();
    }
}
