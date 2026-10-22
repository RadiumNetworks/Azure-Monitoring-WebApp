using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisChartTests
{
    [Theory]
    [InlineData(10, 20, 22)]
    [InlineData(80, 80, 88)]
    [InlineData(0, 0, 1)]
    [InlineData(0.001, 0.002, 0.0022)]
    public void StartsAtZeroAndAddsTenPercentHeadroom(double first, double second, double expectedMaximum)
    {
        var chart = AnalysisChartGeometry.Create(Response(first, second));

        Assert.Equal(expectedMaximum, chart.Maximum, 10);
        Assert.Equal("0", chart.ValueTicks[0].Label);
        Assert.Equal("305", chart.ValueTicks[0].Position);
        Assert.Equal("35", chart.ValueTicks[^1].Position);
        Assert.Equal(6, chart.ValueTicks.Count);
        Assert.Equal(6, chart.TimeTicks.Count);
    }

    [Fact]
    public void PlacesZeroOnBaselineAndUsesFullRequestedTimeRange()
    {
        var chart = AnalysisChartGeometry.Create(Response(0, 20));

        Assert.StartsWith("100,305 ", Assert.Single(chart.Lines));
        Assert.EndsWith("975,59.55", chart.Lines[0]);
        Assert.Equal("10:00", chart.TimeTicks[0].Label);
        Assert.Equal("11:00", chart.TimeTicks[^1].Label);
        Assert.Equal("19.10.2026", chart.TimeTicks[0].DateLabel);
    }

    [Fact]
    public void FormatsSvgCoordinatesIndependentlyOfGermanCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var chart = AnalysisChartGeometry.Create(Response(0, 20));

            Assert.EndsWith("975,59.55", Assert.Single(chart.Lines));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task RendersValueScaleGridAndUtcTimeline()
    {
        var html = await RenderAsync(Response(10, 20));

        Assert.Contains("analysis-chart-value-tick", html);
        Assert.Contains("analysis-chart-time-tick", html);
        Assert.Contains("analysis-chart-gridline", html);
        Assert.Contains(">22</text>", html);
        Assert.Contains(">0</text>", html);
        Assert.Contains("10:00", html);
        Assert.Contains("11:00", html);
        Assert.Contains("19.10.2026", html);
        Assert.Contains("Time (UTC)", html);
        Assert.Contains("Percent", html);
    }

    [Fact]
    public async Task EmptySeriesShowsAnExplicitNoDataMessage()
    {
        var response = Response(0, 0) with
        {
            Series = [new("SERVER01", null, "Workspace A", [])]
        };

        var html = await RenderAsync(response);

        Assert.Contains("returned no time-series rows", html);
        Assert.DoesNotContain("<svg", html);
    }

    [Fact]
    public async Task AllowsSwitchingBetweenChartAndAllResultRowsWithoutChangingTheResponse()
    {
        var response = Response(10, 20);
        response = response with
        {
            Series =
            [
                response.Series[0],
                new("SERVER02", "CPU0", "Workspace B",
                    [new(response.From.AddMinutes(30), -2.125, "<test>")])
            ]
        };
        var chart = await RenderAsync(response, allowTableView: true);
        var table = await RenderAsync(response, allowTableView: true, showTable: true);
        var chartAgain = await RenderAsync(response, allowTableView: true);

        Assert.Contains("role=\"switch\"", chart);
        Assert.Contains("<svg", chart);
        Assert.DoesNotContain("<table", chart);
        Assert.Contains("<table", table);
        Assert.DoesNotContain("<svg", table);
        Assert.Contains("Timestamp (UTC)", table);
        Assert.Contains("2026-10-19 10:30:00", table);
        Assert.Contains("SERVER01", table);
        Assert.Contains("SERVER02", table);
        Assert.Contains("CPU0", table);
        Assert.Contains("Workspace B", table);
        Assert.Contains("-2", table);
        Assert.Contains("&lt;test&gt;", table);
        Assert.Equal(4, Regex.Matches(table, "<tr>").Count);
        Assert.Contains("<svg", chartAgain);
        Assert.DoesNotContain("<table", chartAgain);
        Assert.Equal(2, response.Series.Count);
    }

    private static AnalysisQueryResponse Response(double first, double second)
    {
        var from = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        return new(Guid.NewGuid(), "CPU", 1, "Percent", from, from.AddHours(1), from.AddHours(1), 10,
            [new("SERVER01", null, "Workspace A",
                [new(from, first, null), new(from.AddHours(1), second, null)])], []);
    }

    private static async Task<string> RenderAsync(
        AnalysisQueryResponse response, bool allowTableView = false, bool showTable = false)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<AnalysisResultView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Result"] = response, ["AllowTableView"] = allowTableView, ["ShowTable"] = showTable
                }));
            return rendered.ToHtmlString();
        });
    }
}
