namespace MonitoringApp.Tests;

public sealed class AlertTimeZonesTests
{
    [Theory]
    [InlineData("utc", 12)]
    [InlineData("germany", 14)]
    [InlineData("singapore", 20)]
    [InlineData("new-york", 8)]
    public void ConvertsSummerUtcTimestampToSelectedZone(string timeZoneId, int expectedHour)
    {
        var timestamp = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

        var result = AlertTimeZones.ToDisplayTime(timestamp, timeZoneId);

        Assert.Equal(new DateTime(2026, 8, 22, expectedHour, 0, 0), result);
    }

    [Theory]
    [InlineData("germany", 13)]
    [InlineData("new-york", 7)]
    public void AppliesWinterDaylightSavingOffset(string timeZoneId, int expectedHour)
    {
        var timestamp = new DateTimeOffset(2026, 1, 22, 12, 0, 0, TimeSpan.Zero);

        var result = AlertTimeZones.ToDisplayTime(timestamp, timeZoneId);

        Assert.Equal(new DateTime(2026, 1, 22, expectedHour, 0, 0), result);
    }

    [Fact]
    public void ChangingZonePreservesFilterInstant()
    {
        var utcFilter = new DateTime(2026, 8, 22, 12, 30, 0);

        var germanyFilter = AlertTimeZones.ConvertWallClock(utcFilter, "utc", "germany");
        var roundTrip = AlertTimeZones.ConvertWallClock(germanyFilter, "germany", "utc");

        Assert.Equal(new DateTime(2026, 8, 22, 14, 30, 0), germanyFilter);
        Assert.Equal(utcFilter, roundTrip);
    }

    [Fact]
    public void WallClockInputRoundTripsIndependentlyOfCurrentCulture()
    {
        var value = new DateTime(2026, 8, 22, 12, 30, 0);

        var formatted = AlertTimeZones.FormatWallClock(value);
        var parsed = AlertTimeZones.ParseWallClock(formatted);

        Assert.Equal("2026-08-22T12:30", formatted);
        Assert.Equal(value, parsed);
        Assert.Equal(value, AlertTimeZones.ParseWallClock(value));
        Assert.Equal(value, AlertTimeZones.ParseWallClock("2026-08-22T12:30:00"));
    }
}