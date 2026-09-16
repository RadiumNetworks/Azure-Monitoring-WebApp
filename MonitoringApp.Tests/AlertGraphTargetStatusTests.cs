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

    [Theory]
    [InlineData(-1)]
    [InlineData(-20)]
    public void HeartbeatUpToTwentyMinutesOldIsCurrent(int minutes)
    {
        var node = TargetNode(count: 0, CurrentTime.AddMinutes(minutes));

        Assert.Equal(AlertGraphTargetStatus.Current, AlertGraphTargetStatusResolver.Resolve(node, CurrentTime));
    }

    [Fact]
    public void MissingOrOlderHeartbeatIsStale()
    {
        Assert.Equal(AlertGraphTargetStatus.Stale, AlertGraphTargetStatusResolver.Resolve(TargetNode(0, null), CurrentTime));
        Assert.Equal(AlertGraphTargetStatus.Stale, AlertGraphTargetStatusResolver.Resolve(TargetNode(0, CurrentTime.AddMinutes(-21)), CurrentTime));
    }

    [Fact]
    public void StatusDoesNotApplyOutsideTargetLayerThree()
    {
        var node = TargetNode(0, CurrentTime.AddMinutes(-1)) with { Level = 2 };

        Assert.Equal(AlertGraphTargetStatus.None, AlertGraphTargetStatusResolver.Resolve(node, CurrentTime));
    }

    private static AlertGraphNode TargetNode(int count, DateTimeOffset? heartbeat) =>
        new("target", "SERVER01", AlertGraphLayer.Target, 3, count, 0, 0, 0, heartbeat);
}