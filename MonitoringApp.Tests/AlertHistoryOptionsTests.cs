namespace MonitoringApp.Tests;

public sealed class AlertHistoryOptionsTests
{
    private static readonly AlertHistoryOptionsTestCases TestCases =
        TestCaseLoader.Load<AlertHistoryOptionsTestCases>("alert-history-options.json");

    public static IEnumerable<object[]> InvalidDays =>
        TestCases.InvalidDays.Select(days => new object[] { days });

    [Fact]
    public void DefaultsToSevenDayCutoff()
    {
        var cutoff = new AlertHistoryOptions().GetCutoff(TestCases.ReferenceTime);

        Assert.Equal(TestCases.ExpectedCutoff, cutoff);
    }

    [Theory]
    [MemberData(nameof(InvalidDays))]
    public void RejectsInvalidHistoryDays(int days)
    {
        var options = new AlertHistoryOptions { Days = days };

        Assert.NotEmpty(options.Validate());
    }
}