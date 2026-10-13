namespace MonitoringApp.Tests;

public sealed class InboxTargetOutputTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan DefaultAge = TimeSpan.FromMinutes(5);

    [Fact]
    public void InventoryWithoutHeartbeatOrAlertsAppearsAsMissing()
    {
        var cell = Assert.Single(InboxTargetOutput.Build([], [], [Inventory(null)], Now, DefaultAge));

        Assert.Equal("SERVER01", cell.Target);
        Assert.Equal("Site A", cell.Site);
        Assert.Equal("sub-a", cell.SubscriptionId);
        Assert.Equal(0, cell.Count);
        Assert.True(cell.MissingHeartbeat);
        Assert.Equal("heatmap-cell heatmap-cell-missing-heartbeat", cell.CssClass);
    }

    [Fact]
    public void MaintenanceHidesBothOpenAlertsAndMissingHeartbeat()
    {
        var entry = Inventory(null);
        entry.Maintenance = true;
        var alert = Alert();
        Assert.Empty(InboxTargetOutput.Build([alert], [alert], [entry], Now, DefaultAge));
        Assert.Empty(InboxTargetOutput.Build([], [], [entry], Now, DefaultAge));

        entry.Maintenance = false;
        Assert.False(Assert.Single(InboxTargetOutput.Build([alert], [alert], [entry], Now, DefaultAge)).MissingHeartbeat);
        Assert.True(Assert.Single(InboxTargetOutput.Build([], [], [entry], Now, DefaultAge)).MissingHeartbeat);
    }

    [Theory]
    [InlineData(1, 1, 0, false)]
    [InlineData(1, 1, -1, true)]
    [InlineData(5, 5, 0, false)]
    [InlineData(5, 5, -1, true)]
    [InlineData(10, 6, 0, false)]
    [InlineData(10, 10, -1, true)]
    public void UsesRuleMinutesAndExactHeartbeatBoundary(int limit, int age, int ticks, bool missing)
    {
        var rule = new AlertRule { RuleType = AlertRuleTypes.InventoryHeartbeatFreshness, Threshold = limit };
        var cells = InboxTargetOutput.Build(
            [], [], [Inventory(Now.AddMinutes(-age).AddTicks(ticks))],
            Now, InventoryHeartbeatFreshnessRule.Resolve([rule]));

        Assert.Equal(missing ? 1 : 0, cells.Count);
        Assert.All(cells, cell => Assert.True(cell.MissingHeartbeat));
    }

    [Fact]
    public void OpenAlertsTakePrecedenceOverMissingHeartbeatAndKeepCount()
    {
        var alerts = new[] { Alert(), Alert() };
        var cell = Assert.Single(InboxTargetOutput.Build(
            alerts, AlertLifecycle.GetActiveAlerts(alerts), [Inventory(null)], Now, DefaultAge));

        Assert.Equal(2, cell.Count);
        Assert.False(cell.MissingHeartbeat);
        Assert.Equal("heatmap-cell", cell.CssClass);
        Assert.Equal(InboxTargetColor.Orange, cell.Color);
    }

    [Fact]
    public void NonRunningServiceWithoutAlertCreatesPurpleHealthAlert()
    {
        var inventory = Inventory(Now.AddMinutes(-1));
        inventory.ServicesJson = """[{"Name":"NTDS","Status":1}]""";

        var cell = Assert.Single(InboxTargetOutput.Build([], [], [inventory], Now, DefaultAge));

        Assert.True(cell.HasServiceHealthAlert);
        Assert.False(cell.MissingHeartbeat);
        Assert.Equal(0, cell.Count);
        Assert.Equal(InboxTargetColor.Purple, cell.Color);
        Assert.Contains("heatmap-cell-service-health-alert", cell.CssClass, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalAlertTakesPrecedenceOverServiceHealthAlert()
    {
        var inventory = Inventory(Now.AddMinutes(-1));
        inventory.ServicesJson = """[{"Name":"NTDS","Status":1}]""";
        var alert = Alert();

        var cell = Assert.Single(InboxTargetOutput.Build([alert], [alert], [inventory], Now, DefaultAge));

        Assert.False(cell.HasServiceHealthAlert);
        Assert.Equal(1, cell.Count);
        Assert.Equal(InboxTargetColor.Yellow, cell.Color);
    }

    [Fact]
    public void ResolvedLifecycleDoesNotPreventMissingHeartbeatInHistory()
    {
        var fired = Alert() with { ReceivedAt = Now.AddHours(-2) };
        var resolved = fired with { Id = Guid.NewGuid(), ReceivedAt = Now.AddHours(-1), MonitorCondition = "Resolved" };
        var alerts = new[] { fired, resolved };
        var cell = Assert.Single(InboxTargetOutput.Build(
            alerts, AlertLifecycle.GetActiveAlerts(alerts), [Inventory(Now.AddMinutes(-6))], Now, DefaultAge));

        Assert.True(cell.MissingHeartbeat);
        Assert.Equal(2, cell.Count);
    }

    [Fact]
    public void ManuallyResolvedAlertDoesNotPreventMissingHeartbeat()
    {
        var alert = Alert() with { Comments = "Resolved" };
        var cell = Assert.Single(InboxTargetOutput.Build(
            [alert], AlertLifecycle.GetActiveAlerts([alert]), [Inventory(null)], Now, DefaultAge));

        Assert.True(cell.MissingHeartbeat);
    }

    [Fact]
    public void SystemWithOnlyResolvedAlertsAppearsWhenActiveViewHasNoAlerts()
    {
        var alert = Alert() with { MonitorCondition = "Resolved" };
        var active = AlertLifecycle.GetActiveAlerts([alert]);
        var cell = Assert.Single(InboxTargetOutput.Build(active, active, [Inventory(null)], Now, DefaultAge));

        Assert.Equal(0, cell.Count);
        Assert.True(cell.MissingHeartbeat);
    }

    [Fact]
    public void HiddenOpenAlertStillPreventsGrayStatus()
    {
        Assert.Empty(InboxTargetOutput.Build([], [Alert()], [Inventory(null)], Now, DefaultAge));
    }

    [Fact]
    public void OpenAlertMatchesSystemCaseInsensitively()
    {
        var alert = Alert("SUB-A", "server01");
        var cell = Assert.Single(InboxTargetOutput.Build([alert], [alert], [Inventory(null)], Now, DefaultAge));

        Assert.False(cell.MissingHeartbeat);
    }

    [Fact]
    public void SameDomainAndTargetAcrossSubscriptionsUseLatestSubscription()
    {
        var alert = Alert() with { ReceivedAt = Now.AddMinutes(-2) };
        var oldInventory = Inventory(Now.AddMinutes(-3));
        var latestInventory = Inventory(Now.AddMinutes(-1), "sub-b");
        oldInventory.Domain = "Example.Test";
        latestInventory.Domain = "example.test";
        var cells = InboxTargetOutput.Build(
            [alert], [alert], [oldInventory, latestInventory], Now, DefaultAge);

        var cell = Assert.Single(cells);
        Assert.Equal("sub-b", cell.SubscriptionId);
        Assert.Equal("example.test", cell.Domain);
        Assert.False(cell.MissingHeartbeat);
        Assert.Equal(1, cell.Count);
    }

    [Fact]
    public void InventoryDoesNotDuplicateDisplayedTargetWhenSitesDiffer()
    {
        var alert = Alert() with { MonitorCondition = "Resolved", DisplayIdentity = new("SERVER01", "Old site") };
        var cell = Assert.Single(InboxTargetOutput.Build([alert], [], [Inventory(null)], Now, DefaultAge));

        Assert.True(cell.MissingHeartbeat);
        Assert.Equal("Old site", cell.Site);
    }

    [Fact]
    public void SupplementalCellsRespectInventoryFilter()
    {
        var older = Inventory(Now.AddMinutes(-7));
        var latest = Inventory(Now.AddMinutes(-6), "sub-b");
        var cells = InboxTargetOutput.Build(
            [], [], [older, latest], Now, DefaultAge,
            entry => entry.SubscriptionId == "sub-b");

        Assert.Equal("sub-b", Assert.Single(cells).SubscriptionId);
    }

    [Fact]
    public void CrossingBoundaryDoesNotNeedNewInventoryData()
    {
        var inventory = new[] { Inventory(Now.AddMinutes(-5)) };

        Assert.Empty(InboxTargetOutput.Build([], [], inventory, Now, DefaultAge));
        Assert.True(Assert.Single(InboxTargetOutput.Build([], [], inventory, Now.AddTicks(1), DefaultAge)).MissingHeartbeat);
    }

    [Fact]
    public void AlertsWithoutInventoryRetainExistingDensityAndFallbackSite()
    {
        var alert = Alert() with { DisplayIdentity = new("SERVER01", "") };
        var cell = Assert.Single(InboxTargetOutput.Build([alert], [], [], Now, DefaultAge));

        Assert.Equal(1, cell.Count);
        Assert.Equal("Unknown site", cell.Site);
        Assert.False(cell.MissingHeartbeat);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveHeartbeatLimit(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InboxTargetOutput.Build([], [], [], Now, TimeSpan.FromMinutes(minutes)));
    }

    [Theory]
    [InlineData(1, InboxTargetColor.Yellow)]
    [InlineData(2, InboxTargetColor.Orange)]
    [InlineData(3, InboxTargetColor.Red)]
    [InlineData(10, InboxTargetColor.Red)]
    public void UsesAbsoluteColorThresholds(int count, InboxTargetColor expected)
    {
        var alerts = Enumerable.Range(0, count).Select(_ => Alert()).ToArray();
        var other = Enumerable.Range(0, 20).Select(_ => Alert(target: "OTHER")).ToArray();

        var cell = InboxTargetOutput.Build(alerts.Concat(other), alerts.Concat(other), [], Now, DefaultAge)
            .Single(cell => cell.Target == "SERVER01");

        Assert.Equal(expected, cell.Color);
        Assert.Equal(count, cell.Count);
        Assert.Equal(count, cell.NonSuppressedCount);
        Assert.False(cell.SuppressedOnly);
    }

    [Fact]
    public void SuppressedOnlySystemStaysGreenEvenWithManyOpenAlertsAndStaleHeartbeat()
    {
        var alerts = Enumerable.Range(0, 20).Select(_ => Alert()).ToArray();
        var category = Category(alerts, AlertRuleAnalysisClassifications.Suppressed);
        var cell = Assert.Single(InboxTargetOutput.Build(
            alerts, alerts, [Inventory(null)], Now, DefaultAge, categories: [category]));

        Assert.Equal(20, cell.Count);
        Assert.Equal(0, cell.NonSuppressedCount);
        Assert.True(cell.SuppressedOnly);
        Assert.False(cell.MissingHeartbeat);
        Assert.Equal(InboxTargetColor.Green, cell.Color);
        Assert.Equal("--heat-hue:112;--heat-saturation:58%;--heat-lightness:84%", cell.Style);
    }

    [Fact]
    public void SuppressedAlertsDoNotRaiseMixedSystemColorOrHideItsOutage()
    {
        var suppressed = Enumerable.Range(0, 10).Select(_ => Alert()).ToArray();
        var outage = Alert();
        var alerts = suppressed.Append(outage).ToArray();
        var categories = new[]
        {
            Category(suppressed, AlertRuleAnalysisClassifications.Suppressed),
            Category([outage], AlertRuleAnalysisClassifications.SystemOutage)
        };
        var cell = Assert.Single(InboxTargetOutput.Build(
            alerts, alerts, [], Now, DefaultAge, categories: categories));

        Assert.Equal(11, cell.Count);
        Assert.Equal(1, cell.NonSuppressedCount);
        Assert.False(cell.SuppressedOnly);
        Assert.True(cell.IsOutage);
        Assert.False(cell.IsCriticalOutage);
        Assert.Contains("heatmap-cell-outage", cell.CssClass, StringComparison.Ordinal);
        Assert.Equal(InboxTargetColor.Yellow, cell.Color);
    }

    [Fact]
    public void CriticalOutageIsRedEvenForOneAlertAndRenamedCategory()
    {
        var alert = Alert();
        var category = Category([alert], AlertRuleAnalysisClassifications.SystemOutage, critical: true) with
        {
            Name = "Editable category name"
        };
        var cell = Assert.Single(InboxTargetOutput.Build(
            [alert], [alert], [], Now, DefaultAge, categories: [category]));

        Assert.True(cell.IsCriticalOutage);
        Assert.True(cell.IsOutage);
        Assert.Equal(InboxTargetColor.Red, cell.Color);
        Assert.Equal("--heat-hue:0;--heat-saturation:70%;--heat-lightness:58%", cell.Style);
    }

    [Fact]
    public void ResolvedSuppressedHistoryDoesNotOverrideMissingHeartbeat()
    {
        var alert = Alert() with { MonitorCondition = "Resolved" };
        var cell = Assert.Single(InboxTargetOutput.Build(
            [alert], [], [Inventory(null)], Now, DefaultAge,
            categories: [Category([alert], AlertRuleAnalysisClassifications.Suppressed)]));

        Assert.True(cell.SuppressedOnly);
        Assert.True(cell.MissingHeartbeat);
        Assert.Equal(InboxTargetColor.MissingHeartbeat, cell.Color);
    }

    [Fact]
    public void InventoryDomainIsTrimmedAndSeparatesEqualTargets()
    {
        var a = Inventory(null);
        a.Domain = " a.example ";
        var b = Inventory(null, "sub-b");
        b.Domain = "b.example";
        var alert = Alert("SUB-A", "server01");

        var cells = InboxTargetOutput.Build([alert], [alert], [a, b], Now, DefaultAge);

        Assert.Equal("a.example", cells.Single(cell => cell.SubscriptionId == "SUB-A").Domain);
        Assert.Equal("b.example", cells.Single(cell => cell.SubscriptionId == "sub-b").Domain);
        Assert.Equal("Unknown domain",
            Assert.Single(InboxTargetOutput.Build([alert], [alert], [], Now, DefaultAge)).Domain);
    }

    [Fact]
    public void MaintenanceStillHidesClassifiedOutageAndSuppressedAlerts()
    {
        var alert = Alert();
        var entry = Inventory(null);
        entry.Maintenance = true;

        Assert.Empty(InboxTargetOutput.Build(
            [alert], [alert], [entry], Now, DefaultAge,
            categories: [Category([alert], AlertRuleAnalysisClassifications.SystemOutage, critical: true)]));
    }

    private static AlertCategoryGroup Category(
        IReadOnlyList<AlertRecord> alerts, string classification, bool critical = false) =>
        new(Guid.NewGuid(), "Category", "info", true, alerts, classification, critical);

    private static ComputerInventoryEntry Inventory(DateTimeOffset? heartbeat, string subscription = "sub-a") => new()
    {
        SubscriptionId = subscription,
        Computer = "SERVER01",
        Site = "Site A",
        LastHeartbeatAt = heartbeat
    };

    private static AlertRecord Alert(string subscription = "sub-a", string target = "SERVER01") => new(
        Guid.NewGuid(), Now, Guid.NewGuid().ToString(), "Directory outage", "Sev1", "Fired", "Log", "Fired",
        target, "resource-group", subscription, Now, "", "", "", "{}")
    {
        DisplayIdentity = new(target, "Site A")
    };
}
