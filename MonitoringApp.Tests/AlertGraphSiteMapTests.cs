namespace MonitoringApp.Tests;

public sealed class AlertGraphSiteMapTests
{
    private static readonly AlertGraphSiteLocationOptions[] DefaultLocations =
        AlertGraphSiteLocationOptions.CreateDefaults();
    private static readonly DateTimeOffset NoonUtc =
        new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShowsAllLocationsAsHealthyWithoutAlerts()
    {
        var markers = AlertGraphSiteMap.Build([], DefaultLocations, NoonUtc);

        Assert.Equal(["New York", "Singapore", "Frankfurt", "London"], markers.Select(marker => marker.Name));
        Assert.All(markers, marker =>
        {
            Assert.False(marker.HasOpenAlerts);
            Assert.Equal("No open alerts", marker.StatusLabel);
            Assert.InRange(marker.X, 0, AlertGraphSiteMap.Width);
            Assert.InRange(marker.Y, 0, AlertGraphSiteMap.Height);
        });
    }

    [Fact]
    public void MarksOnlySitesWithOpenAlertsAsAlerting()
    {
        var records = new[]
        {
            Record("new-york-open", "Fired", "New York"),
            Record("london-resolved", "Resolved", "London"),
            Record("frankfurt-comment-resolved", "Fired", "Frankfurt", "Resolved by operator")
        };

        var markers = AlertGraphSiteMap.Build(records, DefaultLocations, NoonUtc);

        Assert.True(Marker(markers, "New York").HasOpenAlerts);
        Assert.Equal(1, Marker(markers, "New York").OpenAlertCount);
        Assert.False(Marker(markers, "London").HasOpenAlerts);
        Assert.False(Marker(markers, "Frankfurt").HasOpenAlerts);
        Assert.False(Marker(markers, "Singapore").HasOpenAlerts);
    }

    [Fact]
    public void UsesLatestLifecycleEventAndRecognizesSiteAliases()
    {
        var older = Record("shared-alert", "Fired", "Singapure") with
        {
            ReceivedAt = new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero)
        };
        var newer = older with
        {
            Id = Guid.NewGuid(),
            ReceivedAt = older.ReceivedAt.AddMinutes(5),
            MonitorCondition = "Resolved"
        };

        var markers = AlertGraphSiteMap.Build(
            [older, newer, Record("nyc-alert", "Fired", "NYC")],
            DefaultLocations,
            NoonUtc);

        Assert.False(Marker(markers, "Singapore").HasOpenAlerts);
        Assert.True(Marker(markers, "New York").HasOpenAlerts);
    }

    [Fact]
    public void UsesConfiguredDisplayNameCoordinatesAndAliases()
    {
        var locations = new[]
        {
            new AlertGraphSiteLocationOptions
            {
                DisplayName = "Operations North",
                Latitude = 52.5,
                Longitude = 13.4,
                Aliases = ["AD-SITE-NORTH"]
            }
        };

        var marker = Assert.Single(AlertGraphSiteMap.Build(
            [Record("custom-alert", "Fired", "AD-SITE-NORTH")],
            locations,
            NoonUtc));

        Assert.Equal("Operations North", marker.Name);
        Assert.True(marker.HasOpenAlerts);
        Assert.InRange(marker.X, 0, AlertGraphSiteMap.Width);
        Assert.InRange(marker.Y, 0, AlertGraphSiteMap.Height);
    }

    private static AlertGraphSiteMarker Marker(
        IEnumerable<AlertGraphSiteMarker> markers,
        string name) =>
        Assert.Single(markers, marker => marker.Name == name);

    private static AlertGraphRecord Record(
        string alertId,
        string condition,
        string site,
        string comments = "") =>
        new(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero),
            alertId,
            condition,
            comments,
            "Example",
            "subscription-a",
            "resource-group-a",
            $"target-{alertId}",
            site,
            "example.test",
            "domaincontrollers");
}
