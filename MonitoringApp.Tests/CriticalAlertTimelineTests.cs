namespace MonitoringApp.Tests;

public sealed class CriticalAlertTimelineTests
{
    private static readonly CriticalAlertTimelineTestCases TestCases =
        TestCaseLoader.Load<CriticalAlertTimelineTestCases>("critical-alert-timeline.json");

    public static TheoryData<CriticalAlertTimelineCase> Cases =>
        new(TestCases.Cases);

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProducesExpectedHourlyCounts(CriticalAlertTimelineCase testCase)
    {
        var lifecycles = testCase.Lifecycles.Select(lifecycle => new CriticalAlertLifecycle(
            lifecycle.AlertId,
            lifecycle.StartedAt,
            lifecycle.ResolvedAt));

        var buckets = CriticalAlertTimeline.GetHourlyCounts(
            lifecycles,
            testCase.FirstHour,
            testCase.Hours,
            testCase.Now);

        Assert.Equal(testCase.ExpectedCounts, buckets.Select(bucket => bucket.Count));
    }

    [Fact]
    public void ResolvedAlertsRemainInHistoricalPeakButNotInCurrentCount()
    {
        var firstHour = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var buckets = CriticalAlertTimeline.GetHourlyCounts(
            [
                new("first", firstHour, firstHour.AddHours(2)),
                new("second", firstHour, firstHour.AddHours(3))
            ],
            firstHour, 48, firstHour.AddHours(47).AddMinutes(30));

        Assert.Equal(2, buckets.Max(bucket => bucket.Count));
        Assert.Equal(0, buckets[^1].Count);
    }

    [Fact]
    public void RejectsEmptyChartWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CriticalAlertTimeline.GetHourlyCounts(
                [],
                DateTimeOffset.UnixEpoch,
                TestCases.InvalidHours,
                DateTimeOffset.UnixEpoch));
    }
}
