using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MonitoringApp.Migrations;

namespace MonitoringApp.Tests;

public sealed class RcaDisplayOptionsTests
{
    [Theory]
    [InlineData("Open", "", "#b42318")]
    [InlineData("Open", " ", "#b42318")]
    [InlineData("Open", "admin@example.org", "#a64b00")]
    [InlineData("InAnalysis", "", "#a64b00")]
    [InlineData("InAnalysis", "admin@example.org", "#a64b00")]
    [InlineData("Actions", "", "#a64b00")]
    [InlineData("Closed", "", "#137547")]
    [InlineData("Closed", "admin@example.org", "#137547")]
    public void DefaultColorsDependOnWorkflowAndAssignmentNotCriticality(
        string status, string assignedTo, string color)
    {
        var options = new RcaDisplayOptions();
        foreach (var active in new[] { true, false })
            Assert.Equal($"--rca-color:{color}",
                options.Style(new() { Status = status, AssignedTo = assignedTo, IsActive = active }));
    }

    [Fact]
    public void ConfiguredColorsAreCaseInsensitiveAndNeverRenderedAsRawCss()
    {
        var options = new RcaDisplayOptions
        {
            OpenUnassignedColor = "GREEN", ClosedColor = "Orange", OtherColor = "gray"
        };
        Assert.Empty(options.Validate());
        Assert.Equal("--rca-color:#137547", options.Style(new()));
        Assert.Equal("--rca-color:#a64b00", options.Style(new() { Status = RcaStatuses.Closed }));
        Assert.Equal("--rca-color:#58645e", options.Style(new() { AssignedTo = "admin@example.org" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("purple")]
    [InlineData("red; background:url(https://example.org)")]
    public void RejectsInvalidColorsInEverySetting(string? color)
    {
        var options = DatabaseSettingsLoader.Deserialize<RcaDisplayOptions>(
            DatabaseSettingsLoader.RcaDisplay, JsonSerializer.Serialize(new
            {
                OpenUnassignedColor = color, ClosedColor = color, OtherColor = color
            }));
        Assert.Equal(3, options.Validate().Count);
    }

    [Fact]
    public void MigrationSeedsRequiredSettingWithMatchingDefaultsAndRoundTrip()
    {
        var migration = new AddRcaDisplaySetting();
        var operation = Assert.IsType<SqlOperation>(Assert.Single(migration.UpOperations));
        Assert.Contains("IF NOT EXISTS", operation.Sql);
        var json = operation.Sql[operation.Sql.IndexOf('{')..(operation.Sql.LastIndexOf('}') + 1)];
        var options = DatabaseSettingsLoader.Deserialize<RcaDisplayOptions>(DatabaseSettingsLoader.RcaDisplay, json);
        var restored = DatabaseSettingsLoader.Deserialize<RcaDisplayOptions>(
            DatabaseSettingsLoader.RcaDisplay, JsonSerializer.Serialize(options));
        Assert.Empty(restored.Validate());
        Assert.Equal("red", restored.OpenUnassignedColor);
        Assert.Equal("green", restored.ClosedColor);
        Assert.Equal("orange", restored.OtherColor);
        Assert.Contains(DatabaseSettingsLoader.RcaDisplay, DatabaseSettingsLoader.RequiredNames);
        var down = Assert.IsType<DeleteDataOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal("Settings", down.Table);
        Assert.Equal("RcaDisplay", down.KeyValues[0, 0]);
    }
}
