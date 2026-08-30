namespace MonitoringApp.Tests;

public sealed class AlertSeverityDisplayOptionsTests
{
    [Fact]
    public void ResolvesConfiguredSeverityAndDefaultClasses()
    {
        var options = ValidOptions();

        Assert.Equal("severity-color-red severity-style-bold", options.CssClass("sev0"));
        Assert.Equal("severity-color-black severity-style-normal", options.CssClass("unknown"));
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void RejectsUnsupportedColorAndFontStyle()
    {
        var options = new AlertSeverityDisplayOptions
        {
            Severities = [new() { Severity = "Sev0", Color = "blue", FontStyle = "italic" }]
        };

        var errors = options.Validate();

        Assert.Contains(errors, error => error.Contains("Color 'blue'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("FontStyle 'italic'", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsDuplicateSeverityNamesIgnoringCase()
    {
        var options = new AlertSeverityDisplayOptions
        {
            Severities =
            [
                new() { Severity = "Sev1", Color = "red", FontStyle = "bold" },
                new() { Severity = "sev1", Color = "yellow", FontStyle = "normal" }
            ]
        };

        Assert.Contains(options.Validate(), error => error.Contains("duplicate Severity", StringComparison.Ordinal));
    }

    private static AlertSeverityDisplayOptions ValidOptions() => new()
    {
        Severities = [new() { Severity = "Sev0", Color = "red", FontStyle = "bold" }],
        Default = new() { Color = "black", FontStyle = "normal" }
    };
}