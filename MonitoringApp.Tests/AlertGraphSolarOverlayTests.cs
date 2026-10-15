namespace MonitoringApp.Tests;

public sealed class AlertGraphSolarOverlayTests
{
    [Fact]
    public void EquinoxNoonPlacesGreenwichInDaylightAndAntimeridianAtNight()
    {
        var time = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

        var greenwich = AlertGraphSolarOverlay.ResolvePhase(time, 0, 0);
        var antimeridian = AlertGraphSolarOverlay.ResolvePhase(time, 0, 180);

        Assert.Equal(AlertGraphSolarPhase.Day, greenwich);
        Assert.Equal(AlertGraphSolarPhase.Night, antimeridian);
    }

    [Fact]
    public void SolsticeSubsolarLatitudeIsInExpectedHemisphere()
    {
        var june = AlertGraphSolarOverlay.Build(
            new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var december = AlertGraphSolarOverlay.Build(
            new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero));

        Assert.InRange(june.SubsolarLatitude, 22, 24);
        Assert.InRange(december.SubsolarLatitude, -24, -22);
    }

    [Fact]
    public void BuildsFiniteNightAndTwilightSvgPaths()
    {
        var overlay = AlertGraphSolarOverlay.Build(
            new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

        Assert.NotEmpty(overlay.Paths);
        Assert.Contains(overlay.Paths, path => path.Phase == AlertGraphSolarPhase.Night);
        Assert.Contains(overlay.Paths, path => path.Phase == AlertGraphSolarPhase.Twilight);
        Assert.All(overlay.Paths, path =>
        {
            Assert.StartsWith("M ", path.Data);
            Assert.EndsWith(" Z", path.Data);
            Assert.DoesNotContain("NaN", path.Data, StringComparison.Ordinal);
            Assert.DoesNotContain("Infinity", path.Data, StringComparison.Ordinal);
        });
    }
}
