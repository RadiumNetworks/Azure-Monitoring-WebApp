namespace MonitoringApp.Tests;

public sealed class AlertGraphOptionsTests
{
    private static readonly AlertGraphOptionsTestCases TestCases =
        TestCaseLoader.Load<AlertGraphOptionsTestCases>("alert-graph-options.json");

    [Fact]
    public void IncludesRequestedLayerChoices()
    {
        var options = TestCases.Valid;

        Assert.Contains(options.Layer1, choice => choice.Value == AlertGraphLayer.ResourceGroup);
        Assert.Contains(options.Layer3, choice => choice.Value == AlertGraphLayer.Site);
        Assert.Equal(4, options.SiteLocations.Length);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void RejectsDefaultMissingFromLayerChoices()
    {
        Assert.Contains(
            TestCases.MissingDefault.Validate(),
            error => error.Contains(TestCases.MissingDefaultError, StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsDuplicateAliasesAcrossSiteLocations()
    {
        var options = new AlertGraphOptions
        {
            Layer1 = [new(AlertGraphLayer.Subscription, "Subscription")],
            Layer2 = [new(AlertGraphLayer.Site, "Site")],
            Layer3 = [new(AlertGraphLayer.Target, "Target")],
            DefaultLayer1 = AlertGraphLayer.Subscription,
            DefaultLayer2 = AlertGraphLayer.Site,
            DefaultLayer3 = AlertGraphLayer.Target,
            SiteLocations =
            [
                new() { DisplayName = "First", Aliases = ["Shared"] },
                new() { DisplayName = "Second", Aliases = ["shared"] }
            ]
        };

        Assert.Contains(
            options.Validate(),
            error => error.Contains("aliases must be unique across locations", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsInvalidSiteCoordinates()
    {
        var valid = TestCases.Valid;
        var options = new AlertGraphOptions
        {
            Layer1 = valid.Layer1,
            Layer2 = valid.Layer2,
            Layer3 = valid.Layer3,
            DefaultLayer1 = valid.DefaultLayer1,
            DefaultLayer2 = valid.DefaultLayer2,
            DefaultLayer3 = valid.DefaultLayer3,
            SiteLocations =
            [
                new AlertGraphSiteLocationOptions
                {
                    DisplayName = "Invalid",
                    Latitude = 91,
                    Longitude = -181,
                    Aliases = ["Invalid"]
                }
            ]
        };

        var errors = options.Validate();

        Assert.Contains(errors, error => error.Contains("Latitude", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Longitude", StringComparison.Ordinal));
    }
}