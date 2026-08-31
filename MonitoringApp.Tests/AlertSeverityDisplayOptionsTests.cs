namespace MonitoringApp.Tests;

public sealed class AlertSeverityDisplayOptionsTests
{
    private static readonly AlertSeverityDisplayOptionsTestCases TestCases =
        TestCaseLoader.Load<AlertSeverityDisplayOptionsTestCases>("alert-severity-display-options.json");

    [Fact]
    public void ResolvesConfiguredSeverityAndDefaultClasses()
    {
        var options = TestCases.Valid;

        Assert.Equal(TestCases.ExpectedConfiguredClass, options.CssClass(TestCases.ConfiguredSeverity));
        Assert.Equal(TestCases.ExpectedDefaultClass, options.CssClass(TestCases.UnknownSeverity));
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void RejectsUnsupportedColorAndFontStyle()
    {
        var errors = TestCases.Unsupported.Validate();

        foreach (var expectedError in TestCases.UnsupportedErrors)
        {
            Assert.Contains(errors, error => error.Contains(expectedError, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RejectsDuplicateSeverityNamesIgnoringCase()
    {
        Assert.Contains(
            TestCases.Duplicates.Validate(),
            error => error.Contains(TestCases.DuplicateError, StringComparison.Ordinal));
    }
}