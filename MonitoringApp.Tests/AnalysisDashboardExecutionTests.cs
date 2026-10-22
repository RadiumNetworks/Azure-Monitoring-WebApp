using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardExecutionTests
{
    [Theory]
    [InlineData(null, null, 240, 30)]
    [InlineData(60, null, 60, 30)]
    [InlineData(null, 15, 240, 15)]
    [InlineData(10080, 60, 10080, 60)]
    public void ResolvesOverridesIndependentlyWithoutChangingWidgets(
        int? timeRange, int? bin, int expectedTimeRange, int expectedBin)
    {
        var document = new AnalysisDashboardDocument { TimeRangeMinutesOverride = timeRange, BinMinutesOverride = bin };
        var widget = new AnalysisDashboardWidget
        {
            QueryDefinitionId = Guid.NewGuid(), TimeRangeMinutes = 240, BinMinutes = 30, GroupBy = "NC"
        };
        AnalysisQuerySystem[] systems = [new("sub", "DC01")];

        var request = document.CreateExecutionRequest(widget, systems);

        Assert.Equal(expectedTimeRange, request.TimeRangeMinutes);
        Assert.Equal(expectedBin, request.BinMinutes);
        Assert.Equal(widget.QueryDefinitionId, request.QueryDefinitionId);
        Assert.Same(systems, request.Systems);
        Assert.Equal("NC", request.GroupBy);
        Assert.Equal(240, widget.TimeRangeMinutes);
        Assert.Equal(30, widget.BinMinutes);
    }

    [Fact]
    public void LegacyDocumentsKeepWidgetValuesAndNullQueryDefaultInterval()
    {
        var document = JsonSerializer.Deserialize<AnalysisDashboardDocument>("{}")!;
        var widget = new AnalysisDashboardWidget { TimeRangeMinutes = 480, BinMinutes = null };
        var request = document.CreateExecutionRequest(widget, []);
        Assert.Equal(480, request.TimeRangeMinutes);
        Assert.Null(request.BinMinutes);
        Assert.Null(document.TimeRangeMinutesOverride);
        Assert.Null(document.BinMinutesOverride);
        widget.BinMinutes = 5;
        Assert.Equal(5, document.CreateExecutionRequest(widget, []).BinMinutes);
    }

    [Fact]
    public void OverridesApplyToEveryWidgetAndCanBeClearedWithoutLosingOriginalValues()
    {
        var state = new AnalysisDashboardState();
        state.AddWidget(Guid.NewGuid(), "CPU", 60);
        state.AddWidget(Guid.NewGuid(), "Replication", 1440);
        state.SetExecutionOverrides(10080, 30);
        Assert.All(state.Document.Widgets, widget =>
        {
            var request = state.Document.CreateExecutionRequest(widget, []);
            Assert.Equal(10080, request.TimeRangeMinutes);
            Assert.Equal(30, request.BinMinutes);
        });
        state.SetExecutionOverrides(null, null);
        Assert.Equal([1440, 60], state.Document.Widgets.Select(widget => state.Document.CreateExecutionRequest(widget, []).TimeRangeMinutes));
        Assert.All(state.Document.Widgets, widget => Assert.Null(widget.BinMinutes));
    }

    [Fact]
    public void PersistsPerDashboardAndSupportsUndoRedoWithoutTouchingWidgetDefaults()
    {
        var collection = AnalysisDashboardCollection.Create();
        var first = collection.ActiveDashboardId;
        var state = new AnalysisDashboardState();
        state.Load(collection.OpenActive());
        state.SetExecutionOverrides(10080, 30);
        state.Undo();
        Assert.Null(state.Document.TimeRangeMinutesOverride);
        Assert.Null(state.Document.BinMinutesOverride);
        state.Redo();
        Assert.Equal(10080, state.Document.TimeRangeMinutesOverride);
        Assert.Equal(30, state.Document.BinMinutesOverride);
        collection.CaptureActive(state.Document);
        collection.Add("Second");
        Assert.Null(collection.OpenActive().TimeRangeMinutesOverride);
        Assert.Null(collection.OpenActive().BinMinutesOverride);
        collection = AnalysisDashboardCollection.Deserialize(collection.Serialize());
        collection.Select(first);
        Assert.Equal(10080, collection.OpenActive().TimeRangeMinutesOverride);
        Assert.Equal(30, collection.OpenActive().BinMinutesOverride);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(5, null)]
    [InlineData(43201, null)]
    [InlineData(null, 0)]
    [InlineData(null, 5)]
    [InlineData(null, 1440)]
    public void RejectsInvalidOverridesWithoutChangingStateOrUndoHistory(int? timeRange, int? bin)
    {
        var state = new AnalysisDashboardState();
        Assert.Throws<InvalidOperationException>(() => state.SetExecutionOverrides(timeRange, bin));
        Assert.Null(state.Document.TimeRangeMinutesOverride);
        Assert.Null(state.Document.BinMinutesOverride);
        Assert.False(state.CanUndo);
        Assert.Throws<InvalidOperationException>(() => new AnalysisDashboardDocument
        {
            TimeRangeMinutesOverride = timeRange, BinMinutesOverride = bin
        }.CreateExecutionRequest(new(), []));
        Assert.Throws<InvalidOperationException>(() => new AnalysisDashboardState().Load(new()
        {
            TimeRangeMinutesOverride = timeRange, BinMinutesOverride = bin
        }));
    }

    [Fact]
    public void DashboardOverridesDoNotBypassPublishedQueryMaximum()
    {
        var document = new AnalysisDashboardDocument { TimeRangeMinutesOverride = 43200, BinMinutesOverride = 30 };
        var query = ActiveDirectoryAnalysisQueries.Create().First();
        query.MaxTimeRangeMinutes = 1440;
        var request = document.CreateExecutionRequest(new() { QueryDefinitionId = query.Id }, [new("sub", "DC01")]);
        var options = new CrossTenantLogAnalyticsOptions { Enabled = true };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));

        var error = Assert.Throws<InvalidOperationException>(() => runner.CreateDraftExecutionPreview(query, "test",
            [new() { SubscriptionId = "sub", Computer = "DC01" }], request.TimeRangeMinutes, request.BinMinutes, request.GroupBy));

        Assert.Contains("Time range must be between 5 and 1440 minutes.", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendersOptionalControlsWithAllSupportedValuesAndSessionNotice(bool sessionOnly)
    {
        var html = await RenderAsync<AnalysisDashboardExecutionControls>(new()
        {
            ["TimeRangeMinutes"] = 10080,
            ["BinMinutes"] = 30,
            ["Disabled"] = true,
            ["SessionOnly"] = sessionOnly
        });
        Assert.Contains("Dashboard time range", html);
        Assert.Contains("Dashboard averaging interval", html);
        Assert.Equal(2, html.Split("Use widget settings").Length - 1);
        Assert.Equal(2, html.Split("disabled").Length - 1);
        foreach (var value in AnalysisQuerySelections.TimeRanges)
            Assert.Contains($"value=\"{value}\"", html);
        foreach (var value in AnalysisQuerySelections.BinSizes)
            Assert.Contains($"value=\"{value}\"", html);
        Assert.Equal(sessionOnly, html.Contains("Changes apply only to this view.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishedWidgetHeaderShowsEffectiveSettingsRatherThanStoredWidgetValues()
    {
        var document = new AnalysisDashboardDocument
        {
            TimeRangeMinutesOverride = 10080, BinMinutesOverride = 30,
            Widgets = [new() { TimeRangeMinutes = 60, BinMinutes = 15 }]
        };
        var html = await RenderAsync<PublishedAnalysisDashboardGrid>(new() { ["Document"] = document });
        Assert.Contains("7 days", html);
        Assert.Contains("30 min", html);
        Assert.DoesNotContain("15 min", html);
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Request validation must not access Azure credentials.");
    }
}
