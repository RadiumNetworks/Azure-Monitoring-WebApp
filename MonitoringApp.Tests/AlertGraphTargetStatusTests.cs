namespace MonitoringApp.Tests;

public sealed class AlertGraphTargetStatusTests
{
    private static readonly DateTimeOffset CurrentTime = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OpenAlertTakesPrecedenceOverCurrentHeartbeat()
    {
        var node = TargetNode(count: 1, CurrentTime.AddMinutes(-1));

        Assert.Equal(AlertGraphTargetStatus.OpenAlert, AlertGraphTargetStatusResolver.Resolve(node, CurrentTime));
    }

    [Fact]
    public void ServiceHealthAlertIsPurpleStatusOnlyWithoutOpenAlert()
    {
        var node = TargetNode(count: 0, CurrentTime.AddMinutes(-1)) with { HasServiceHealthAlert = true };

        Assert.Equal(
            AlertGraphTargetStatus.ServiceHealthAlert,
            AlertGraphTargetStatusResolver.Resolve(node, CurrentTime));
        Assert.Equal(
            AlertGraphTargetStatus.OpenAlert,
            AlertGraphTargetStatusResolver.Resolve(node with { Count = 1 }, CurrentTime));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    public void HeartbeatUpToFiveMinutesOldIsCurrent(int minutes)
    {
        var node = TargetNode(count: 0, CurrentTime.AddMinutes(minutes));

        Assert.Equal(AlertGraphTargetStatus.Current, AlertGraphTargetStatusResolver.Resolve(node, CurrentTime));
    }

    [Fact]
    public void MissingOrOlderHeartbeatIsStale()
    {
        Assert.Equal(AlertGraphTargetStatus.Stale, AlertGraphTargetStatusResolver.Resolve(TargetNode(0, null), CurrentTime));
        Assert.Equal(AlertGraphTargetStatus.Stale, AlertGraphTargetStatusResolver.Resolve(TargetNode(0, CurrentTime.AddMinutes(-5).AddTicks(-1)), CurrentTime));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(20)]
    public void UsesConfiguredHeartbeatBoundary(int minutes)
    {
        var currentHeartbeatAge = InventoryHeartbeatFreshnessRule.Resolve(
            [new AlertRule { RuleType = AlertRuleTypes.InventoryHeartbeatFreshness, Threshold = minutes }]);
        var heartbeat = CurrentTime.AddMinutes(-minutes);

        Assert.Equal(AlertGraphTargetStatus.Current,
            AlertGraphTargetStatusResolver.Resolve(TargetNode(0, heartbeat), CurrentTime, currentHeartbeatAge));
        Assert.Equal(AlertGraphTargetStatus.Stale,
            AlertGraphTargetStatusResolver.Resolve(TargetNode(0, heartbeat.AddTicks(-1)), CurrentTime, currentHeartbeatAge));
        Assert.Equal(AlertGraphTargetStatus.Stale,
            AlertGraphTargetStatusResolver.Resolve(TargetNode(0, null), CurrentTime, currentHeartbeatAge));
        Assert.Equal(AlertGraphTargetStatus.OpenAlert,
            AlertGraphTargetStatusResolver.Resolve(TargetNode(1, heartbeat.AddTicks(-1)), CurrentTime, currentHeartbeatAge));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveHeartbeatBoundary(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AlertGraphTargetStatusResolver.Resolve(TargetNode(0, null), CurrentTime, TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void StatusDoesNotApplyOutsideTargetLayerThree()
    {
        var node = TargetNode(0, CurrentTime.AddMinutes(-1)) with { Level = 2 };

        Assert.Equal(AlertGraphTargetStatus.None, AlertGraphTargetStatusResolver.Resolve(node, CurrentTime));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void MaintenanceTakesPrecedenceOverAlertsAndMissingHeartbeat(int count)
    {
        var node = TargetNode(count, null) with { Maintenance = true };
        Assert.Equal(AlertGraphTargetStatus.Maintenance, AlertGraphTargetStatusResolver.Resolve(node, CurrentTime));
        Assert.Equal(AlertGraphTargetStatus.None,
            AlertGraphTargetStatusResolver.Resolve(node with { Layer = AlertGraphLayer.Site }, CurrentTime));
    }

    private static AlertGraphNode TargetNode(int count, DateTimeOffset? heartbeat) =>
        new("target", "SERVER01", AlertGraphLayer.Target, 3, count, 0, 0, 0, heartbeat);
}