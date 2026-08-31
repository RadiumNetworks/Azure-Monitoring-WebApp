namespace MonitoringApp.Tests;

public sealed class CriticalAlertTimelineTests
{
    [Fact]
    public void CountsCriticalAlertsActiveAtEachHourlySnapshot()
    {
        var firstHour = DateTimeOffset.Parse("2026-08-30T09:00:00Z");
        var now = DateTimeOffset.Parse("2026-08-30T12:30:00Z");
        var lifecycles = new[]
        {
            new CriticalAlertLifecycle(
                "resolved",
                DateTimeOffset.Parse("2026-08-30T09:15:00Z"),
                DateTimeOffset.Parse("2026-08-30T11:30:00Z")),
            new CriticalAlertLifecycle(
                "open",
                DateTimeOffset.Parse("2026-08-30T10:30:00Z"),
                null)
        };

        var buckets = CriticalAlertTimeline.GetHourlyCounts(lifecycles, firstHour, 4, now);

        Assert.Equal([1, 2, 1, 1], buckets.Select(bucket => bucket.Count));
    }

    [Fact]
    public void TreatsAlertAsInactiveAtItsResolutionTime()
    {
        var firstHour = DateTimeOffset.Parse("2026-08-30T09:00:00Z");
        var resolvedAt = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        var lifecycle = new CriticalAlertLifecycle(
            "resolved-on-boundary",
            DateTimeOffset.Parse("2026-08-30T09:15:00Z"),
            resolvedAt);

        var bucket = Assert.Single(CriticalAlertTimeline.GetHourlyCounts(
            [lifecycle],
            firstHour,
            1,
            resolvedAt));

        Assert.Equal(0, bucket.Count);
    }

    [Fact]
    public void RejectsEmptyChartWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CriticalAlertTimeline.GetHourlyCounts([], DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow));
    }
}
