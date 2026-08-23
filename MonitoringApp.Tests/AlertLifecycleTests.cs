namespace MonitoringApp.Tests;

public sealed class AlertLifecycleTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiredAlertIsActive()
    {
        var alert = CreateAlert("alert-1", "Fired");

        var active = AlertLifecycle.GetActiveAlerts([alert]);

        Assert.Same(alert, Assert.Single(active));
    }

    [Fact]
    public void LaterResolvedEventClosesAlert()
    {
        var active = AlertLifecycle.GetActiveAlerts([
            CreateAlert("alert-1", "Fired"),
            CreateAlert("alert-1", "Resolved", minute: 1)
        ]);

        Assert.Empty(active);
    }

    [Fact]
    public void LaterFiredEventReopensAlert()
    {
        var reopened = CreateAlert("alert-1", "Fired", minute: 1);

        var active = AlertLifecycle.GetActiveAlerts([
            CreateAlert("alert-1", "Resolved"),
            reopened
        ]);

        Assert.Same(reopened, Assert.Single(active));
    }

    [Fact]
    public void LegacyDuplicateFiredEventsCountOnceCaseInsensitively()
    {
        var latest = CreateAlert("ALERT-1", "Fired", minute: 1);

        var active = AlertLifecycle.GetActiveAlerts([
            CreateAlert("alert-1", "Fired"),
            latest
        ]);

        Assert.Same(latest, Assert.Single(active));
    }

    [Fact]
    public void AlertsWithoutIdsRemainIndependent()
    {
        var active = AlertLifecycle.GetActiveAlerts([
            CreateAlert(string.Empty, "Fired", target: "target-1"),
            CreateAlert(" ", "Fired", minute: 1, target: "target-2")
        ]);

        Assert.Equal(2, active.Count);
    }

    [Theory]
    [InlineData("Resolved")]
    [InlineData("Resolved by operator")]
    [InlineData("manually RESOLVED after validation")]
    public void StandaloneResolvedCommentClosesAlert(string comments)
    {
        var active = AlertLifecycle.GetActiveAlerts([
            CreateAlert("alert-1", "Fired", comments: comments)
        ]);

        Assert.Empty(active);
    }

    [Fact]
    public void UnresolvedCommentDoesNotCloseAlert()
    {
        var alert = CreateAlert("alert-1", "Fired", comments: "Still unresolved");

        var active = AlertLifecycle.GetActiveAlerts([alert]);

        Assert.Same(alert, Assert.Single(active));
    }

    [Fact]
    public void NavigationHierarchyCountsLogicalOpenAlertsCumulatively()
    {
        var active = AlertLifecycle.GetActiveAlerts([
            CreateAlert("open", "Fired", subscription: "sub-a", resourceGroup: "rg-1", target: "target-open"),
            CreateAlert("resolved", "Fired", subscription: "sub-a", resourceGroup: "rg-1", target: "target-resolved"),
            CreateAlert("resolved", "Resolved", minute: 1, subscription: "sub-a", resourceGroup: "rg-1", target: "target-resolved"),
            CreateAlert("manual", "Fired", comments: "Resolved by operator", subscription: "sub-a", resourceGroup: "rg-1", target: "target-manual"),
            CreateAlert("duplicate", "Fired", subscription: "sub-a", resourceGroup: "rg-2", target: "target-duplicate"),
            CreateAlert("DUPLICATE", "Fired", minute: 1, subscription: "sub-a", resourceGroup: "rg-2", target: "target-duplicate"),
            CreateAlert(string.Empty, "Fired", subscription: "sub-b", resourceGroup: "rg-3", target: "target-no-id-1"),
            CreateAlert(string.Empty, "Fired", minute: 1, subscription: "sub-b", resourceGroup: "rg-3", target: "target-no-id-2"),
            CreateAlert("reopened", "Resolved", subscription: "sub-b", resourceGroup: "rg-4", target: "target-reopened"),
            CreateAlert("reopened", "Fired", minute: 1, subscription: "sub-b", resourceGroup: "rg-4", target: "target-reopened")
        ]);

        Assert.Equal(5, active.Count);
        Assert.Equal(2, Count(active, subscription: "sub-a"));
        Assert.Equal(1, Count(active, subscription: "sub-a", resourceGroup: "rg-1"));
        Assert.Equal(1, Count(active, subscription: "sub-a", resourceGroup: "rg-2"));
        Assert.Equal(1, Count(active, subscription: "sub-a", resourceGroup: "rg-2", target: "target-duplicate"));
        Assert.Equal(3, Count(active, subscription: "sub-b"));
        Assert.Equal(2, Count(active, subscription: "sub-b", resourceGroup: "rg-3"));
        Assert.Equal(1, Count(active, subscription: "sub-b", resourceGroup: "rg-4"));
    }

    [Fact]
    public void NavigationIncludesOnlyBranchesSeenDuringLastSevenDays()
    {
        var cutoff = BaseTime.AddDays(-7);
        var atCutoff = CreateAlert("at-cutoff", "Fired", subscription: "recent-sub", resourceGroup: "recent-rg", target: "recent-target") with
        {
            ReceivedAt = cutoff
        };
        var old = CreateAlert("old", "Fired", subscription: "old-sub", resourceGroup: "old-rg", target: "old-target") with
        {
            ReceivedAt = cutoff.AddTicks(-1)
        };
        var resolved = CreateAlert("resolved", "Resolved", subscription: "resolved-sub", resourceGroup: "resolved-rg", target: "resolved-target");
        var alerts = new[] { atCutoff, old, resolved };

        var navigation = AlertNavigation.Build(alerts, AlertLifecycle.GetActiveAlerts(alerts), cutoff);

        Assert.Equal(["recent-sub", "resolved-sub"], navigation.Select(node => node.Name));
        Assert.Equal(1, navigation.Single(node => node.Name == "recent-sub").Count);
        Assert.Equal(1, navigation.Single(node => node.Name == "recent-sub").HistoryCount);

        var resolvedSubscription = navigation.Single(node => node.Name == "resolved-sub");
        Assert.Equal(0, resolvedSubscription.Count);
        Assert.Equal(1, resolvedSubscription.HistoryCount);
        Assert.Equal(1, Assert.Single(resolvedSubscription.ResourceGroups).HistoryCount);
        Assert.Equal(1, Assert.Single(Assert.Single(resolvedSubscription.ResourceGroups).Targets).HistoryCount);
        Assert.DoesNotContain(navigation, node => node.Name == "old-sub");
    }

    private static int Count(
        IEnumerable<AlertRecord> alerts,
        string subscription,
        string? resourceGroup = null,
        string? target = null) => alerts.Count(alert =>
            alert.SubscriptionId == subscription &&
            (resourceGroup is null || alert.ResourceGroup == resourceGroup) &&
            (target is null || alert.TargetName == target));

    private static AlertRecord CreateAlert(
        string alertId,
        string condition,
        int minute = 0,
        string comments = "",
        string subscription = "sub",
        string resourceGroup = "rg",
        string target = "target") => new(
            Guid.NewGuid(),
            BaseTime.AddMinutes(minute),
            alertId,
            "Test alert",
            "Sev2",
            string.Empty,
            "Metric",
            condition,
            target,
            resourceGroup,
            subscription,
            BaseTime,
            string.Empty,
            string.Empty,
            comments,
            "{}");
}