namespace MonitoringApp.Tests;

public sealed class AlertHistoryOptionsTests
{
    [Fact]
    public void DefaultsToSevenDayCutoff()
    {
        var referenceTime = new DateTimeOffset(2000, 1, 8, 12, 0, 0, TimeSpan.Zero);

        var cutoff = new AlertHistoryOptions().GetCutoff(referenceTime);

        Assert.Equal(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero), cutoff);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public void RejectsInvalidHistoryDays(int days)
    {
        var options = new AlertHistoryOptions { Days = days };

        Assert.NotEmpty(options.Validate());
    }
}