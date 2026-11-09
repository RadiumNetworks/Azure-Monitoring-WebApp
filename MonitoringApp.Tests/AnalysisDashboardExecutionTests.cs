using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        Assert.Equal(timeRange.HasValue ? AnalysisValidationCode.DashboardTimeRangeInvalid : AnalysisValidationCode.DashboardIntervalInvalid,
            Assert.Throws<AnalysisValidationException>(() => state.SetExecutionOverrides(timeRange, bin)).Code);
        Assert.Null(state.Document.TimeRangeMinutesOverride);
        Assert.Null(state.Document.BinMinutesOverride);
        Assert.False(state.CanUndo);
        Assert.Throws<AnalysisValidationException>(() => new AnalysisDashboardDocument
        {
            TimeRangeMinutesOverride = timeRange, BinMinutesOverride = bin
        }.CreateExecutionRequest(new(), []));
        state.SetExecutionOverrides(60, 15);
        var before = state.Document;
        Assert.Throws<AnalysisValidationException>(() => state.Load(new()
        {
            TimeRangeMinutesOverride = timeRange, BinMinutesOverride = bin
        }));
        Assert.Same(before, state.Document);
        Assert.True(state.CanUndo);
        Assert.Equal(60, state.Document.TimeRangeMinutesOverride);
        Assert.Equal(15, state.Document.BinMinutesOverride);
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

        var error = Assert.Throws<AnalysisValidationException>(() => runner.CreateDraftExecutionPreview(query, "test",
            [new() { SubscriptionId = "sub", Computer = "DC01" }], request.TimeRangeMinutes, request.BinMinutes, request.GroupBy));

        Assert.Equal(AnalysisValidationCode.TimeRangeInvalid, error.Code);
        Assert.Contains("Time range must be between 5 and 1440 minutes.", error.Message);
    }

    [Theory]
    [InlineData("en-US", false, false)]
    [InlineData("en-US", false, true)]
    [InlineData("en-US", true, false)]
    [InlineData("en-US", true, true)]
    [InlineData("de-DE", false, false)]
    [InlineData("de-DE", false, true)]
    [InlineData("de-DE", true, false)]
    [InlineData("de-DE", true, true)]
    public async Task RendersOptionalControlsWithAllSupportedValuesAndSessionNotice(string culture, bool sessionOnly, bool disabled)
    {
        using var selection = new UiSelection(culture);
        var html = WebUtility.HtmlDecode(await RenderAsync<AnalysisDashboardExecutionControls>(new()
        {
            ["TimeRangeMinutes"] = 10080,
            ["BinMinutes"] = 30,
            ["Disabled"] = disabled,
            ["SessionOnly"] = sessionOnly
        }));
        Assert.Contains(culture == "de-DE" ? "Dashboard-Ausführungseinstellungen" : "Dashboard execution settings", html);
        Assert.Contains(culture == "de-DE" ? "Dashboardzeitraum" : "Dashboard time range", html);
        Assert.Contains(culture == "de-DE" ? "Dashboard-Mittelungsintervall" : "Dashboard averaging interval", html);
        Assert.Equal(2, html.Split(culture == "de-DE" ? "Widgeteinstellungen verwenden" : "Use widget settings").Length - 1);
        Assert.Equal(disabled ? 2 : 0, html.Split("disabled").Length - 1);
        Assert.Contains(culture == "de-DE"
            ? "Überschreibungen gelten beim nächsten Lauf für alle Widgets, auch bei automatischer Aktualisierung. Der maximale Zeitraum einer Abfrage gilt weiterhin; eine Überschreitung führt zu einem Widgetfehler."
            : "Overrides apply to all widgets on the next run, including auto-refresh. A query's maximum time range still applies; exceeding it produces a widget error.", html);
        foreach (var value in AnalysisQuerySelections.TimeRanges)
            Assert.Contains($"value=\"{value}\"", html);
        foreach (var value in AnalysisQuerySelections.BinSizes)
            Assert.Contains($"value=\"{value}\"", html);
        Assert.Equal(sessionOnly, html.Contains(culture == "de-DE"
            ? "Änderungen gelten nur für diese Ansicht. Beim erneuten Öffnen des Dashboards oder Laden einer neuen veröffentlichten Version werden die veröffentlichten Standardwerte wiederhergestellt."
            : "Changes apply only to this view. Reopening the dashboard or loading a new published version restores the published defaults.", StringComparison.Ordinal));
        Assert.Contains(culture == "de-DE" ? ">7 Tage</option>" : ">7 days</option>", html);
        Assert.Contains("value=\"10080\" selected", html);
        Assert.Contains("value=\"30\" selected", html);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    public async Task PublishedWidgetHeaderShowsEffectiveSettingsRatherThanStoredWidgetValues(string culture, bool overrides)
    {
        using var selection = new UiSelection(culture);
        var document = new AnalysisDashboardDocument
        {
            TimeRangeMinutesOverride = overrides ? 10080 : null, BinMinutesOverride = overrides ? 30 : null,
            Widgets = [new() { Title = "Original <widget> &", TimeRangeMinutes = 2880, BinMinutes = null, GroupBy = "NC" }]
        };
        var original = JsonSerializer.Serialize(document);
        var rawHtml = await RenderAsync<PublishedAnalysisDashboardGrid>(new() { ["Document"] = document });
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains(overrides
            ? culture == "de-DE" ? "7 Tage · 30 min · NC" : "7 days · 30 min · NC"
            : culture == "de-DE" ? "2 Tage · Standardintervall der Abfrage · NC" : "2 days · Query default interval · NC", html);
        Assert.Contains("Original &lt;widget&gt; &amp;", rawHtml);
        Assert.DoesNotContain("15 min", html);
        Assert.Equal(original, JsonSerializer.Serialize(document));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false, false)]
    [InlineData("en-US", false, true)]
    [InlineData("en-US", true, false)]
    [InlineData("en-US", true, true)]
    [InlineData("de-DE", false, false)]
    [InlineData("de-DE", false, true)]
    [InlineData("de-DE", true, false)]
    [InlineData("de-DE", true, true)]
    public async Task ExecutionSelectionsKeepTechnicalValuesLimitsAndGroupingKeys(string culture, bool disabled, bool grouping)
    {
        using var selection = new UiSelection(culture);
        var html = WebUtility.HtmlDecode(await RenderAsync<AnalysisExecutionSelections>(new()
        {
            ["TimeRangeMinutes"] = 123, ["BinMinutes"] = 5, ["GroupBy"] = "SourceDSA",
            ["MaxTimeRangeMinutes"] = 240, ["Disabled"] = disabled, ["ShowGrouping"] = grouping
        }));
        Assert.Contains(culture == "de-DE" ? "<span>Zeitraum</span>" : "<span>Time range</span>", html);
        Assert.Contains(culture == "de-DE" ? "<span>Mittelungsintervall</span>" : "<span>Averaging interval</span>", html);
        Assert.Equal(grouping, html.Contains(culture == "de-DE" ? "Replikationsgruppierung" : "Replication grouping"));
        var selects = Regex.Matches(html, "<select\\b([^>]*)>(.*?)</select>", RegexOptions.Singleline);
        Assert.Equal(grouping ? 3 : 2, selects.Count);
        Assert.All(selects.Cast<Match>(), select => Assert.Equal(disabled, select.Groups[1].Value.Contains("disabled")));
        var timeOptions = Regex.Matches(selects[0].Groups[2].Value, "<option\\b([^>]*)>(.*?)</option>", RegexOptions.Singleline);
        var minutes = AnalysisQuerySelections.TimeRanges.Append(123).Order().ToArray();
        Assert.Equal(minutes.Length, timeOptions.Count);
        for (var index = 0; index < minutes.Length; index++)
        {
            Assert.Contains($"value=\"{minutes[index]}\"", timeOptions[index].Groups[1].Value);
            Assert.Equal(minutes[index] > 240, timeOptions[index].Groups[1].Value.Contains("disabled"));
        }
        Assert.Contains(">123 min</option>", html);
        Assert.Contains(">1h</option>", html);
        foreach (var days in new[] { 2, 7, 30 })
            Assert.Contains(culture == "de-DE" ? $">{days} Tage</option>" : $">{days} days</option>", html);
        var binOptions = Regex.Matches(selects[1].Groups[2].Value, "<option\\b[^>]*value=\"([^\"]+)\"[^>]*>(.*?)</option>");
        Assert.Equal(new[] { "5", "15", "30", "60" }, binOptions.Select(option => option.Groups[1].Value));
        Assert.Equal(new[] { "5 min", "15 min", "30 min", "1h" }, binOptions.Select(option => option.Groups[2].Value));
        if (grouping)
        {
            var groupOptions = Regex.Matches(selects[2].Groups[2].Value, "<option\\b[^>]*value=\"([^\"]+)\"[^>]*>(.*?)</option>");
            Assert.Equal(AnalysisQuerySelections.Groupings.Keys, groupOptions.Select(option => option.Groups[1].Value));
            Assert.Equal(culture == "de-DE"
                ? new[] { "Namenskontext", "Quell-DC", "Ziel-DC", "Quellstandort", "Zielstandort" }
                : AnalysisQuerySelections.Groupings.Values, groupOptions.Select(option => option.Groups[2].Value));
        }
        Assert.Equal("Naming Context", AnalysisQuerySelections.Groupings["NC"]);
        Assert.Equal("7 days", AnalysisQuerySelections.TimeRangeLabel(10080));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SelectionCallbacksKeepTypedValuesOrderingAndNullableOverrides(string culture)
    {
        using var selection = new UiSelection(culture);
        var activator = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var calls = new List<string>();
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AnalysisExecutionSelections>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["TimeRangeMinutesChanged"] = EventCallback.Factory.Create<int>(calls, value => calls.Add($"time:{value}")),
                ["ShowGrouping"] = true,
                ["BinMinutesChanged"] = EventCallback.Factory.Create<int>(calls, value => calls.Add($"bin:{value}")),
                ["GroupByChanged"] = EventCallback.Factory.Create<string>(calls, value => calls.Add($"group:{value}")),
                ["Changed"] = EventCallback.Factory.Create(calls, () => calls.Add("changed"))
            })));
        var selections = Assert.IsType<AnalysisExecutionSelections>(activator.Component);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var (method, value) in new[] { ("ChangeTimeRange", "10080"), ("ChangeBin", "30"), ("ChangeGrouping", "DestDSASite") })
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)typeof(AnalysisExecutionSelections)
                .GetMethod(method, flags)!.Invoke(selections, [new ChangeEventArgs { Value = value }])!);
        Assert.Equal(new[] { "time:10080", "changed", "bin:30", "changed", "group:DestDSASite", "changed" }, calls);
        Assert.Equal(1440, selections.TimeRangeMinutes);
        Assert.Equal(15, selections.BinMinutes);
        Assert.Equal("SourceDSA", selections.GroupBy);

        var changes = new List<(int? TimeRangeMinutes, int? BinMinutes)>();
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AnalysisDashboardExecutionControls>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["TimeRangeMinutes"] = 10080, ["BinMinutes"] = 30,
                ["Changed"] = EventCallback.Factory.Create<(int? TimeRangeMinutes, int? BinMinutes)>(changes, value => changes.Add(value))
            })));
        var controls = Assert.IsType<AnalysisDashboardExecutionControls>(activator.Component);
        foreach (var (method, value) in new (string, int?)[]
            { ("ChangeTimeRangeAsync", null), ("ChangeTimeRangeAsync", 60), ("ChangeBinAsync", null), ("ChangeBinAsync", 15) })
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)typeof(AnalysisDashboardExecutionControls)
                .GetMethod(method, flags)!.Invoke(controls, [value])!);
        Assert.Equal(new (int?, int?)[] { (null, 30), (60, 30), (10080, null), (10080, 15) }, changes);
        Assert.Equal(10080, controls.TimeRangeMinutes);
        Assert.Equal(30, controls.BinMinutes);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class CapturingComponentActivator : IComponentActivator
    {
        public IComponent? Component { get; private set; }
        public IComponent CreateInstance(Type componentType) => Component = (IComponent)Activator.CreateInstance(componentType)!;
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo culture = CultureInfo.CurrentCulture;
        public UiSelection(string name)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = uiCulture;
            CultureInfo.CurrentCulture = culture;
        }
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Request validation must not access Azure credentials.");
    }
}
