namespace MonitoringApp.Tests;

public sealed class AlertTimeZoneJsonTests
{
    private static readonly AlertTimeZoneTestCases Cases =
        TestCaseLoader.Load<AlertTimeZoneTestCases>("alert-time-zones.json");
    public static IEnumerable<object[]> ConversionCaseIndexes => Indexes(Cases.Conversions.Count);

    [Theory]
    [MemberData(nameof(ConversionCaseIndexes))]
    public void ConvertsUtcTimestampsToConfiguredZones(int caseIndex)
    {
        var testCase = Cases.Conversions[caseIndex];
        Assert.Equal(testCase.Expected, AlertTimeZones.ToDisplayTime(testCase.Timestamp, testCase.TimeZoneId));
    }

    [Fact]
    public void ChangingZonePreservesFilterInstant()
    {
        var testCase = Cases.WallClockConversion;
        var converted = AlertTimeZones.ConvertWallClock(testCase.Value, testCase.SourceZone, testCase.DestinationZone);
        var roundTrip = AlertTimeZones.ConvertWallClock(converted, testCase.DestinationZone, testCase.SourceZone);

        Assert.Equal(testCase.Expected, converted);
        Assert.Equal(testCase.Value, roundTrip);
    }

    [Fact]
    public void WallClockInputRoundTripsIndependentlyOfCurrentCulture()
    {
        var testCase = Cases.WallClockFormat;
        Assert.Equal(testCase.ExpectedText, AlertTimeZones.FormatWallClock(testCase.Value));
        foreach (var value in testCase.ParseValues)
        {
            Assert.Equal(testCase.Value, AlertTimeZones.ParseWallClock(value));
        }
        Assert.Equal(testCase.Value, AlertTimeZones.ParseWallClock(testCase.Value));
    }

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}