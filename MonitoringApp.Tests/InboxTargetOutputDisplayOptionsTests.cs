using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MonitoringApp.Migrations;

namespace MonitoringApp.Tests;

public sealed class InboxTargetOutputDisplayOptionsTests
{
    [Fact]
    public void MigrationDefaultMatchesExistingDisplayAndRoundTripsThroughDatabaseJson()
    {
        var operation = Assert.IsType<SqlOperation>(
            Assert.Single(new AddInboxTargetOutputDisplaySetting().UpOperations));
        Assert.Contains("IF NOT EXISTS", operation.Sql, StringComparison.Ordinal);
        var start = operation.Sql.IndexOf('{');
        var json = operation.Sql[start..(operation.Sql.LastIndexOf('}') + 1)];
        var options = DatabaseSettingsLoader.Deserialize<InboxTargetOutputDisplayOptions>(
            DatabaseSettingsLoader.InboxTargetOutputDisplay, json);

        Assert.Empty(options.Validate());
        Assert.Equal("green", options.SuppressedColor);
        Assert.True(options.CriticalOutageAlwaysRed);
        Assert.True(options.BoldOutageNames);
        Assert.True(options.ShowDomain);
        Assert.Contains(DatabaseSettingsLoader.InboxTargetOutputDisplay, DatabaseSettingsLoader.RequiredNames);
        foreach (var count in Enumerable.Range(1, 6))
        {
            var cell = Cell(count);
            Assert.Equal(cell.Color, options.Color(cell));
            Assert.Equal(cell.Style, options.Style(cell));
            Assert.Equal(cell.CssClass, options.CssClass(cell));
        }

        var restored = DatabaseSettingsLoader.Deserialize<InboxTargetOutputDisplayOptions>(
            DatabaseSettingsLoader.InboxTargetOutputDisplay, JsonSerializer.Serialize(options));
        Assert.Empty(restored.Validate());
        Assert.Equal(options.Legend(), restored.Legend());
    }

    [Theory]
    [InlineData(1, InboxTargetColor.Orange)]
    [InlineData(2, InboxTargetColor.Orange)]
    [InlineData(3, InboxTargetColor.Yellow)]
    [InlineData(4, InboxTargetColor.Yellow)]
    [InlineData(5, InboxTargetColor.Gray)]
    [InlineData(20, InboxTargetColor.Gray)]
    public void UsesCustomUnorderedThresholdsAtExactBoundaries(int count, InboxTargetColor expected)
    {
        var options = CustomOptions();

        Assert.Empty(options.Validate());
        Assert.Equal(expected, options.Color(Cell(count)));
    }

    [Fact]
    public void LegendReflectsConfiguredRangesSuppressedColorAndCriticalOverride()
    {
        var options = CustomOptions();

        Assert.Equal(
            ["Suppressed", "1-2", "3-4", "5+", "Critical outage", "Service health alert"],
            options.Legend().Select(entry => entry.Label));
        Assert.Equal(
            [InboxTargetColor.Yellow, InboxTargetColor.Orange, InboxTargetColor.Yellow,
                InboxTargetColor.Gray, InboxTargetColor.Red, InboxTargetColor.Purple],
            options.Legend().Select(entry => entry.Color));
        var suppressed = Cell(0) with { Count = 10, SuppressedOnly = true };
        Assert.Equal(InboxTargetColor.Yellow, options.Color(suppressed));
    }

    [Fact]
    public void SwitchesCriticalOverrideFontEmphasisAndDomainWithoutChangingAlertData()
    {
        var cell = Cell(1) with { IsOutage = true, IsCriticalOutage = true, Domain = "corp.example" };
        var defaults = new InboxTargetOutputDisplayOptions();
        var options = new InboxTargetOutputDisplayOptions
        {
            CriticalOutageAlwaysRed = false,
            BoldOutageNames = false,
            ShowDomain = false
        };

        Assert.Equal(InboxTargetColor.Red, defaults.Color(cell));
        Assert.Contains("heatmap-cell-outage", defaults.CssClass(cell), StringComparison.Ordinal);
        Assert.Equal(InboxTargetColor.Yellow, options.Color(cell));
        Assert.DoesNotContain("heatmap-cell-outage", options.CssClass(cell), StringComparison.Ordinal);
        Assert.Contains("heatmap-cell-no-domain", options.CssClass(cell), StringComparison.Ordinal);
        Assert.DoesNotContain(options.Legend(), entry => entry.Label == "Critical outage");
        Assert.True(cell.IsCriticalOutage);
        Assert.Equal("corp.example", cell.Domain);
    }

    [Fact]
    public void HeartbeatGrayStillOverridesConfiguredColorsForHistoricalAlerts()
    {
        var cell = Cell(20) with { MissingHeartbeat = true, IsCriticalOutage = true, SuppressedOnly = true };
        var options = CustomOptions();

        Assert.Equal(InboxTargetColor.MissingHeartbeat, options.Color(cell));
        Assert.Empty(options.Style(cell));
        Assert.Contains("heatmap-cell-missing-heartbeat", options.CssClass(cell), StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceHealthAlertUsesPurpleWithoutOverridingNormalAlerts()
    {
        var options = CustomOptions();
        var healthAlert = Cell(0) with { HasServiceHealthAlert = true, MissingHeartbeat = true };

        Assert.Equal(InboxTargetColor.Purple, options.Color(healthAlert));
        Assert.Contains("heatmap-cell-service-health-alert", options.CssClass(healthAlert), StringComparison.Ordinal);
        Assert.Equal(
            InboxTargetColor.Orange,
            options.Color(healthAlert with
            {
                Count = 1,
                NonSuppressedCount = 1,
                MissingHeartbeat = false
            }));
    }

    [Theory]
    [InlineData("""{"CountColors":null}""")]
    [InlineData("""{"CountColors":[]}""")]
    [InlineData("""{"CountColors":[null]}""")]
    [InlineData("""{"CountColors":[{"MinimumCount":0,"Color":"red"}]}""")]
    [InlineData("""{"CountColors":[{"MinimumCount":-1,"Color":"red"}]}""")]
    [InlineData("""{"CountColors":[{"MinimumCount":2,"Color":"red"}]}""")]
    [InlineData("""{"CountColors":[{"MinimumCount":1,"Color":"yellow"},{"MinimumCount":1,"Color":"red"}]}""")]
    [InlineData("""{"CountColors":[{"MinimumCount":1,"Color":null}]}""")]
    [InlineData("""{"CountColors":[{"MinimumCount":1,"Color":"red; background:url(example)"}]}""")]
    [InlineData("""{"SuppressedColor":null}""")]
    [InlineData("""{"SuppressedColor":"purple"}""")]
    public void RejectsInvalidOrUnsafeSettingsBeforeSaving(string json)
    {
        var options = DatabaseSettingsLoader.Deserialize<InboxTargetOutputDisplayOptions>(
            DatabaseSettingsLoader.InboxTargetOutputDisplay, json);

        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData("""{"ShowDomain":"yes"}""")]
    [InlineData("""{"CountColors":"yellow"}""")]
    [InlineData("null")]
    [InlineData("not JSON")]
    public void RejectsMalformedSettingsExplicitly(string json)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseSettingsLoader.Deserialize<InboxTargetOutputDisplayOptions>(
                DatabaseSettingsLoader.InboxTargetOutputDisplay, json));
    }

    [Fact]
    public void ColorNamesAreCaseInsensitiveAndNeverRenderedAsRawCss()
    {
        var options = new InboxTargetOutputDisplayOptions
        {
            SuppressedColor = "GREEN",
            CountColors = [new() { MinimumCount = 1, Color = "OrAnGe" }]
        };

        Assert.Empty(options.Validate());
        Assert.Equal("--heat-hue:28;--heat-saturation:78%;--heat-lightness:70%", options.Style(Cell(1)));
        Assert.Equal(InboxTargetColor.Green, options.Color(Cell(0) with { SuppressedOnly = true }));
    }

    private static InboxTargetOutputDisplayOptions CustomOptions() => new()
    {
        CountColors =
        [
            new() { MinimumCount = 5, Color = "gray" },
            new() { MinimumCount = 1, Color = "orange" },
            new() { MinimumCount = 3, Color = "yellow" }
        ],
        SuppressedColor = "yellow"
    };

    private static InboxTargetOutputCell Cell(int count) =>
        new("sub-a", "SERVER01", "Site A", count, false, "corp.example", count);
}
