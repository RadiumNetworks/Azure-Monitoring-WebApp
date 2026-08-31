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
