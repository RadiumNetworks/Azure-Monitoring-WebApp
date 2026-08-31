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
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void RejectsDefaultMissingFromLayerChoices()
    {
        Assert.Contains(
            TestCases.MissingDefault.Validate(),
            error => error.Contains(TestCases.MissingDefaultError, StringComparison.Ordinal));
    }
}