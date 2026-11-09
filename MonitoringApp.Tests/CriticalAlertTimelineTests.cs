using System.Reflection;
using System.Text.Json.Nodes;
using MonitoringApp.Components.Pages;

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

    [Fact]
    public void SuppressionOnlyAffectsNowAndDoesNotMutateHistoricalCriticalLifecycles()
    {
        var firstHour = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        CriticalAlertLifecycle[] lifecycles =
        [
            new("suppressed", firstHour, null),
            new("still-critical", firstHour, null),
            new("resolved", firstHour, firstHour.AddHours(2))
        ];
        var before = lifecycles.ToArray();

        var counts = CriticalAlertTimeline.GetHourlyCounts(
            lifecycles, firstHour, 4, firstHour.AddHours(3).AddMinutes(30),
            ["SUPPRESSED", "resolved", "not-in-history"]);

        Assert.Equal([3, 2, 2, 1], counts.Select(bucket => bucket.Count));
        Assert.Equal(3, counts.Max(bucket => bucket.Count));
        Assert.Equal(before, lifecycles);
        Assert.Equal([3, 2, 2, 2], CriticalAlertTimeline.GetHourlyCounts(
            lifecycles, firstHour, 4, firstHour.AddHours(3).AddMinutes(30)).Select(bucket => bucket.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InboxChartKeepsHistoricalPeakButExcludesCurrentlySuppressedAlertsFromNow(bool expandToTarget)
    {
        var now = DateTimeOffset.Parse("2026-11-03T15:30:00Z");
        var started = now.AddHours(-3);
        var healthyPayload = JsonNode.Parse("""{"queryResult":{"type":"DCPort","rows":[]}}""")!.AsObject();
        var failingPayload = JsonNode.Parse(
            """{"queryResult":{"type":"DCPort","rows":[[1],[2],[3],[4],[5],[6],[7],[8],[9],[10],[11]]}}""")!.AsObject();
        AlertRecord Alert(string id, string computer, string condition, DateTimeOffset received, JsonObject payload) =>
            TestAlertFactory.FromFixture(new AlertRecordFixtureCase
            {
                AlertId = id, Target = computer, Name = "Port health alert",
                MonitorCondition = condition, Status = condition, ReceivedAt = received,
                FiredAt = started, Severity = "Sev0", SubscriptionId = "sub-test"
            }, payload: payload);
        var historical = Alert("resolved", "dc-3", "Fired", started, failingPayload);
        List<AlertRecord> alerts =
        [
            Alert("suppressed", "dc-1", "Fired", started, expandToTarget ? failingPayload : healthyPayload),
            Alert("still-critical", "dc-2", "Fired", started, failingPayload),
            historical,
            Alert("resolved", "dc-3", "Resolved", now.AddHours(-1), healthyPayload)
        ];
        if (expandToTarget)
            alerts.Add(Alert("suppression-trigger", "dc-1", "Fired", started.AddMinutes(1), healthyPayload));
        var suppression = DCPortSuppressionRuleTests.CreateDefaultRule();
        suppression.ApplyToTarget = expandToTarget;
        suppression.CategoryName = "Quiet evidence";
        suppression.Priority = 999;
        var rules = new[] { suppression, DefaultAlertRules.Create().Single(rule => rule.CategoryName == "System Outage") };
        IReadOnlyList<CriticalAlertLifecycle> lifecycles =
        [
            new("suppressed", started, null),
            new("still-critical", started, null),
            new("resolved", started, now.AddHours(-1))
        ];
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new Home();
        var type = typeof(Home);
        type.GetProperty("AlertRuleEvaluator", flags)!.SetValue(page,
            new AlertRuleEvaluator(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"))));
        type.GetField("alerts", flags)!.SetValue(page, alerts.ToArray());
        type.GetField("alertRules", flags)!.SetValue(page, rules);
        type.GetField("criticalAlertLifecycles", flags)!.SetValue(page, lifecycles);
        type.GetField("currentTimeUtc", flags)!.SetValue(page, now);

        var buckets = Assert.IsAssignableFrom<IReadOnlyList<HourlyCriticalAlertCount>>(
            type.GetMethod("GetHourlyCriticalAlertCounts", flags)!.Invoke(page, []));

        Assert.Equal(48, buckets.Count);
        Assert.Equal(3, buckets.Max(bucket => bucket.Count));
        Assert.Equal(2, buckets[^2].Count);
        Assert.Equal(1, buckets[^1].Count);
        Assert.Same(lifecycles, type.GetField("criticalAlertLifecycles", flags)!.GetValue(page));
    }
}
