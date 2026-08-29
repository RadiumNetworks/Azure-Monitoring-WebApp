namespace MonitoringApp.Tests;

public sealed class AlertGraphOptionsTests
{
    [Fact]
    public void IncludesRequestedLayerChoices()
    {
        var options = ValidOptions();

        Assert.Contains(options.Layer1, choice => choice.Value == AlertGraphLayer.ResourceGroup);
        Assert.Contains(options.Layer3, choice => choice.Value == AlertGraphLayer.Site);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void RejectsDefaultMissingFromLayerChoices()
    {
        var options = new AlertGraphOptions
        {
            Layer1 = [new(AlertGraphLayer.ResourceGroup, "Resourcegroup")],
            DefaultLayer1 = AlertGraphLayer.Subscription
        };

        Assert.Contains(options.Validate(), error => error.Contains("DefaultLayer1", StringComparison.Ordinal));
    }

    private static AlertGraphOptions ValidOptions() => new()
    {
        Layer1 =
        [
            new(AlertGraphLayer.Subscription, "Subscription"),
            new(AlertGraphLayer.ResourceGroup, "Resourcegroup")
        ],
        Layer2 = [new(AlertGraphLayer.ResourceGroup, "Resourcegroup")],
        Layer3 =
        [
            new(AlertGraphLayer.Target, "Target"),
            new(AlertGraphLayer.Site, "Site")
        ],
        DefaultLayer1 = AlertGraphLayer.Subscription,
        DefaultLayer2 = AlertGraphLayer.ResourceGroup,
        DefaultLayer3 = AlertGraphLayer.Target
    };
}