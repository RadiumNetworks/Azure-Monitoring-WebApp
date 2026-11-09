using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisResultLocalizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 14, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object?[]> StatusLabelCases =>
        from culture in new[] { "en-US", "de-DE" }
        from item in new (string? Value, string Code)[]
        {
            (null, "unknown"), ("", "unknown"), ("  ", "unknown"),
            ("ok", "ok"), ("success", "ok"), ("passed", "ok"), ("running", "ok"), (" OK ", "ok"),
            ("warning", "warning"), ("start pending", "warning"),
            ("error", "error"), ("failed", "error"), ("stopped", "error"),
            ("Unknown", "unknown"), ("Warnung", "unknown"), ("New RCA <original>", "unknown")
        }
        select new object?[] { culture, item.Value, item.Code };

    [Theory]
    [MemberData(nameof(StatusLabelCases))]
    public async Task GeneratedStatusLabelsLocalizeWithoutTranslatingRawOrMappedText(string culture, string? value, string code)
    {
        using var selection = new DisplayCulture(culture);
        var result = Response(AnalysisQueryVisualizationTypes.Table) with
        {
            Tables = [new("Original <workspace>",
                [new("Status", "string"), new("Value", "string"), new("Value__Status", "string"), new("Original", "string")],
                [[value, "Original <value>", value, value]])],
            Visualization = new()
            {
                NullText = "Unknown <configured>",
                Columns = [new() { Name = "Status", ValueMappings = [new(value, "Unknown <mapped>")] }]
            }
        };
        var original = JsonSerializer.Serialize(result);
        var rawHtml = await RenderAsync(result);
        var html = WebUtility.HtmlDecode(rawHtml);
        var label = culture == "de-DE" ? code switch
        {
            "ok" => "OK", "warning" => "Warnung", "error" => "Fehler", _ => "Unbekannt"
        } : code;
        var icons = Regex.Matches(html, "<span class=\"analysis-status analysis-status-([^\"]+)\" aria-label=\"([^\"]+)\" title=\"([^\"]+)\">([^<]+)</span>");
        Assert.Equal(2, icons.Count);
        foreach (Match icon in icons)
        {
            Assert.Equal(code, icon.Groups[1].Value);
            Assert.Equal(label, icon.Groups[2].Value);
            Assert.Equal(label, icon.Groups[3].Value);
            Assert.Equal(AnalysisResultFormatting.StatusSymbol(code), icon.Groups[4].Value);
        }
        Assert.Contains("Unknown <mapped>", html);
        Assert.Contains("Original <value>", html);
        if (value is null) Assert.Contains("Unknown <configured>", html);
        else if (!string.IsNullOrWhiteSpace(value)) Assert.Contains(value, html);
        Assert.DoesNotContain("Value__Status</th>", html);
        Assert.DoesNotContain("<mapped>", rawHtml);
        Assert.DoesNotContain("<original>", rawHtml);
        Assert.Equal(code, AnalysisResultFormatting.Status(value));
        Assert.Equal(original, JsonSerializer.Serialize(result));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("en-US", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 0)]
    [InlineData("de-DE", 1)]
    [InlineData("de-DE", 2)]
    public async Task VisibleChartTicksUseUiCultureButGeometryAndNeutralTicksStayInvariant(string culture, int count)
    {
        using var selection = new DisplayCulture(culture);
        var result = Response(AnalysisQueryVisualizationTypes.LineChart);
        result = result with { Series = [result.Series[0] with
        {
            Points = Enumerable.Range(0, count).Select(index =>
                new AnalysisQueryPoint(Start.AddMinutes(index * 30), 1234.5 + index * 0.125, "Point <original>")).ToArray()
        }] };
        Assert.Equal(count, result.Series[0].Points.Count);
        var original = JsonSerializer.Serialize(result);
        var neutral = AnalysisChartGeometry.Create(result);
        var neutralSnapshot = JsonSerializer.Serialize(neutral);
        var display = AnalysisChartGeometry.CreateForDisplay(result);
        Assert.Equal(neutral.Maximum, display.Maximum);
        Assert.Equal(neutral.Lines, display.Lines);
        Assert.Equal(neutral.TimeTicks, display.TimeTicks);
        Assert.Equal(neutral.ValueTicks.Select(tick => tick.Position), display.ValueTicks.Select(tick => tick.Position));
        for (var index = 0; index < display.ValueTicks.Count; index++)
        {
            var value = neutral.Maximum * index / 5;
            Assert.Equal(value.ToString("G4", CultureInfo.InvariantCulture), neutral.ValueTicks[index].Label);
            Assert.Equal(value.ToString("G4", CultureInfo.GetCultureInfo(culture)), display.ValueTicks[index].Label);
        }
        var rawHtml = await RenderAsync(result);
        if (count == 0) Assert.DoesNotContain("<svg", rawHtml);
        else
        {
            var groups = Regex.Matches(rawHtml, "<g class=\"analysis-chart-value-tick\">(.*?)</g>", RegexOptions.Singleline);
            Assert.Equal(6, groups.Count);
            for (var index = 0; index < groups.Count; index++)
                Assert.Contains($">{display.ValueTicks[index].Label}</text>", groups[index].Value);
            Assert.Contains($"points=\"{neutral.Lines[0]}\"", rawHtml);
        }
        using (new DisplayCulture(culture == "de-DE" ? "en-US" : "de-DE"))
        {
            Assert.Equal(TechnicalAttributes(rawHtml), TechnicalAttributes(await RenderAsync(result)));
            Assert.Equal(neutralSnapshot, JsonSerializer.Serialize(AnalysisChartGeometry.Create(result)));
        }
        Assert.Equal(neutralSnapshot, JsonSerializer.Serialize(AnalysisChartGeometry.Create(result)));
        Assert.Equal(original, JsonSerializer.Serialize(result));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", 0.002)]
    [InlineData("de-DE", 0.002)]
    [InlineData("en-US", 12.345)]
    [InlineData("de-DE", 12.345)]
    [InlineData("en-US", 1234.5)]
    [InlineData("de-DE", 1234.5)]
    [InlineData("en-US", 1234567.89)]
    [InlineData("de-DE", 1234567.89)]
    public async Task DisplayTicksKeepG4PrecisionScaleAndScientificNotation(string culture, double value)
    {
        using var selection = new DisplayCulture(culture);
        var result = Response(AnalysisQueryVisualizationTypes.LineChart);
        result = result with { Series = [result.Series[0] with { Points = [new(Start, value, null)] }] };
        var neutral = AnalysisChartGeometry.Create(result);
        var display = AnalysisChartGeometry.CreateForDisplay(result);
        Assert.Equal(value * 1.1, display.Maximum);
        Assert.Equal(neutral.Lines, display.Lines);
        var html = WebUtility.HtmlDecode(await RenderAsync(result));
        for (var index = 0; index < display.ValueTicks.Count; index++)
        {
            var expected = (display.Maximum * index / 5).ToString("G4", CultureInfo.GetCultureInfo(culture));
            Assert.Equal(expected, display.ValueTicks[index].Label);
            Assert.Contains($">{expected}</text>", html);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task AllVisualizationFramesLocalizeWithoutChangingDataFormattingOrGeometry(string culture)
    {
        using var selection = new DisplayCulture(culture);
        foreach (var type in new[] { AnalysisQueryVisualizationTypes.LineChart, AnalysisQueryVisualizationTypes.Table,
            AnalysisQueryVisualizationTypes.Stat, AnalysisQueryVisualizationTypes.StatusHistory,
            AnalysisQueryVisualizationTypes.Honeycomb, AnalysisQueryVisualizationTypes.BarChart,
            AnalysisQueryVisualizationTypes.BarGauge })
        {
            var result = Response(type);
            var original = JsonSerializer.Serialize(result);
            var rawHtml = await RenderAsync(result);
            var html = WebUtility.HtmlDecode(rawHtml);
            Assert.Contains("Warning <workspace>", html);
            Assert.Contains("New RCA <technical warning>", html);
            Assert.DoesNotContain("<technical warning>", rawHtml);
            Assert.DoesNotContain("Hidden <series>", html);
            if (type == AnalysisQueryVisualizationTypes.Table)
            {
                Assert.Contains("Table view <configured column>", html);
                Assert.Contains("New RCA <original row>", html);
                Assert.Contains("Stopped <mapped value>", html);
                Assert.Contains("analysis-status-error", html);
                Assert.Contains(culture == "de-DE" ? "Abfrageergebnis: Workspace <original>"
                    : "Query result: Workspace <original>", html);
            }
            else if (type == AnalysisQueryVisualizationTypes.Stat)
            {
                Assert.Contains("New RCA <original row>", html);
                Assert.Contains("1234,5", html);
                Assert.Contains("ms <unit>", html);
                Assert.Contains("color:#a12424", html);
            }
            else
            {
                Assert.Contains("Table view <series>", html);
                Assert.Contains("Table view <configured dimension>", html);
                if (type == AnalysisQueryVisualizationTypes.LineChart)
                {
                    Assert.Contains(culture == "de-DE" ? "Zeit (UTC)" : "Time (UTC)", html);
                    Assert.Contains(culture == "de-DE" ? "Legende der Datenreihen" : "Series legend", html);
                    var visible = result with { Series = [result.Series[0]] };
                    Assert.Contains($"points=\"{AnalysisChartGeometry.Create(visible).Lines[0]}\"", rawHtml);
                    Assert.Contains(culture == "de-DE"
                        ? "Table view <query>, 2 Datenreihen von 2026-11-06 14:00:00Z bis 2026-11-06 15:00:00Z"
                        : "Table view <query>, 2 series from 2026-11-06 14:00:00Z to 2026-11-06 15:00:00Z", html);
                }
                else if (type == AnalysisQueryVisualizationTypes.StatusHistory)
                {
                    Assert.Contains(culture == "de-DE" ? "15-Minuten-Intervallen" : "15-minute bins", html);
                    Assert.Contains("2026-11-06 14:00 UTC", html);
                    Assert.Contains("width=\"170\"", html);
                }
                else
                {
                    Assert.Contains("1234,5", html);
                    if (type == AnalysisQueryVisualizationTypes.Honeycomb)
                    {
                        Assert.Contains(culture == "de-DE" ? ">Einpassen</button>" : ">Fit</button>", html);
                        Assert.Contains(culture == "de-DE" ? "Wabendiagramm verkleinern" : "Zoom out honeycomb", html);
                        Assert.Contains(culture == "de-DE" ? "Wabendiagramm vergrößern" : "Zoom in honeycomb", html);
                        Assert.Contains(culture == "de-DE" ? "veröffentlichten Visualisierungseinstellungen"
                            : "published visualization settings", html);
                        Assert.Contains("data-honeycomb-zoom=\"fit\"", html);
                        Assert.Contains("data-honeycomb-error hidden role=\"alert\"", html);
                        Assert.Contains("data-honeycomb-retry hidden", html);
                        Assert.Contains(culture == "de-DE" ? "Wabensteuerung erneut aktivieren"
                            : "Enable honeycomb controls again", html);
                        Assert.Contains(culture == "de-DE" ? "Ergebnisse bleiben verfügbar."
                            : "Results remain available.", html);
                    }
                    else if (type == AnalysisQueryVisualizationTypes.BarGauge)
                    {
                        Assert.Contains("aria-valuenow=\"1234.5\"", html);
                        Assert.Contains("aria-valuemax=\"2000\"", html);
                    }
                }
            }
            string english;
            using (new DisplayCulture("en-US"))
                english = await RenderAsync(result);
            Assert.Equal(TechnicalAttributes(english), TechnicalAttributes(rawHtml));
            Assert.Equal(original, JsonSerializer.Serialize(result));
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task EmptyInvalidAndWorkspaceFramesRemainExplicitAndKeepRawDiagnostics(string culture)
    {
        using var selection = new DisplayCulture(culture);
        Assert.Contains(culture == "de-DE" ? "Führen Sie das Dashboard aus" : "Run the dashboard",
            WebUtility.HtmlDecode(await RenderAsync(null)));
        foreach (var (type, english, german) in new[]
        {
            (AnalysisQueryVisualizationTypes.LineChart, "returned no time-series rows", "keine Zeitreihendaten"),
            (AnalysisQueryVisualizationTypes.Table, "returned no table rows", "keine Tabellenzeilen"),
            (AnalysisQueryVisualizationTypes.Stat, "returned no statistics", "keine Statistiken")
        })
        {
            var result = Response(type) with { Series = [], Tables = [], Scope = AnalysisQueryScopes.Workspace };
            var html = WebUtility.HtmlDecode(await RenderAsync(result));
            Assert.Contains(culture == "de-DE" ? german : english, html);
            Assert.Contains(culture == "de-DE" ? "Arbeitsbereichsweite Ergebnisse" : "Workspace-wide results", html);
            Assert.Contains("New RCA <technical warning>", html);
            Assert.DoesNotContain("<svg", html);
            Assert.DoesNotContain("role=\"switch\"", html);
            var selected = WebUtility.HtmlDecode(await RenderAsync(result with { Scope = AnalysisQueryScopes.SelectedSystems }));
            Assert.DoesNotContain(culture == "de-DE" ? "Arbeitsbereichsweite Ergebnisse" : "Workspace-wide results", selected);
        }
        var statistics = Response(AnalysisQueryVisualizationTypes.Stat) with
        {
            Tables = [new("Original", [new("Series", "string")], [["original"]])]
        };
        Assert.Contains(culture == "de-DE" ? "Statistiken erfordern die Spalten Series und Value."
            : "Statistics require Series and Value columns.", WebUtility.HtmlDecode(await RenderAsync(statistics)));
        Assert.Contains(culture == "de-DE" ? "erfordert das Gruppierungsintervall" : "requires the execution bin interval",
            WebUtility.HtmlDecode(await RenderAsync(Response(AnalysisQueryVisualizationTypes.StatusHistory) with { BinMinutes = null })));
        var honeycomb = Response(AnalysisQueryVisualizationTypes.Honeycomb);
        var sourceOnly = honeycomb with { Series = [honeycomb.Series[0] with { Name = "Source -> " }] };
        var omitted = WebUtility.HtmlDecode(await RenderAsync(sourceOnly));
        Assert.Contains(culture == "de-DE" ? "keine Wabeneinträge" : "No honeycomb entries", omitted);
        Assert.DoesNotContain("<polygon", omitted);
        Assert.DoesNotContain("<svg", omitted);
        var defaultColors = WebUtility.HtmlDecode(await RenderAsync(honeycomb with { Visualization = null }));
        Assert.Contains(culture == "de-DE" ? "LDAP-Latenz: von Grün bei 0 ms bis Rot bei 2000 ms."
            : "LDAP latency: green 0 ms to red 2000 ms.", defaultColors);
        Assert.Contains("hsl(", defaultColors);
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("en-US", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 0)]
    [InlineData("de-DE", 1)]
    [InlineData("de-DE", 2)]
    public async Task ChartDescriptionsUseWholeCountMessagesAndPreserveUtc(string culture, int count)
    {
        using var selection = new DisplayCulture(culture);
        var result = Response(AnalysisQueryVisualizationTypes.LineChart);
        result = result with { Series = Enumerable.Repeat(result.Series[0], count).ToArray() };
        var html = WebUtility.HtmlDecode(await RenderAsync(result));
        if (count == 0)
        {
            Assert.DoesNotContain("<svg", html);
            Assert.Contains(culture == "de-DE" ? "keine Zeitreihendaten" : "no time-series rows", html);
        }
        else
        {
            var counts = culture == "de-DE" ? (count == 1 ? "1 Datenreihe" : "2 Datenreihen") : $"{count} series";
            Assert.Contains(culture == "de-DE"
                ? $"Table view <query>, {counts} von 2026-11-06 14:00:00Z bis 2026-11-06 15:00:00Z"
                : $"Table view <query>, {counts} from 2026-11-06 14:00:00Z to 2026-11-06 15:00:00Z", html);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RealResultSwitchKeepsFallbackTableHeadingsValuesAndResponse(string culture)
    {
        using var selection = new DisplayCulture(culture);
        var activator = new ResultActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var response = Response(AnalysisQueryVisualizationTypes.LineChart);
        var original = JsonSerializer.Serialize(response);
        var events = new List<bool>();
        var parameters = new Dictionary<string, object?>
        {
            ["Result"] = response, ["AllowTableView"] = true,
            ["ShowTableChanged"] = EventCallback.Factory.Create<bool>(this, (bool value) => events.Add(value))
        };
        var root = await renderer.Dispatcher.InvokeAsync(() =>
            renderer.RenderComponentAsync<AnalysisResultView>(ParameterView.FromDictionary(parameters)));
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains(culture == "de-DE" ? ">Tabellenansicht</span>" : ">Table view</span>", html);
        Assert.Contains("<svg", html);
        await renderer.Dispatcher.InvokeAsync(() => activator.Result!.ShowTableChanged.InvokeAsync(true));
        Assert.True(Assert.Single(events));
        await renderer.Dispatcher.InvokeAsync(() => activator.Result!.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["ShowTable"] = true })));
        html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.DoesNotContain("<svg", html);
        foreach (var heading in culture == "de-DE"
            ? new[] { "Zeitstempel (UTC)", "Datenreihe", "Dimension", "Arbeitsbereich", "Wert (ms <unit>)", "Beschriftung" }
            : new[] { "Timestamp (UTC)", "Series", "Dimension", "Workspace", "Value (ms <unit>)", "Label" })
            Assert.Contains($">{heading}</th>", html);
        Assert.Contains("1234,5", html);
        Assert.Contains("2026-11-06 14:00:00", html);
        Assert.Contains("New RCA <point label>", html);
        Assert.Contains("Hidden <series>", html);
        Assert.DoesNotContain("<point label>", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        await renderer.Dispatcher.InvokeAsync(() => activator.Result!.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["ShowTable"] = false })));
        Assert.Contains("<svg", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Equal(original, JsonSerializer.Serialize(response));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task GeneratedWorkspaceNoticesLocalizeWithoutChangingWarningJsonOrUnmarkedText(string culture)
    {
        using var selection = new DisplayCulture(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        foreach (var code in Enum.GetValues<AnalysisQueryNoticeCode>())
        foreach (var loggingFailed in new[] { false, true })
        {
            var id = Guid.NewGuid().ToString("N");
            var plain = new AnalysisQueryWarning("original <workspace>", "private <raw diagnostic>");
            var warning = plain with { Notice = new(code, id, 429, loggingFailed) };
            var result = Response(AnalysisQueryVisualizationTypes.Table) with { Warnings = [warning] };
            var original = JsonSerializer.Serialize(result);
            var (workspace, message) = warning;
            Assert.Equal((plain.Workspace, plain.Message), (workspace, message));
            Assert.Equal(JsonSerializer.Serialize(plain), JsonSerializer.Serialize(warning));
            var expected = code switch
            {
                AnalysisQueryNoticeCode.TableRowLimit => text.Format($"AnalysisWarnings.{code}", AnalysisQueryRunner.MaxTableRows),
                AnalysisQueryNoticeCode.AzureRequestFailed => text.Format($"AnalysisWarnings.{code}", 429),
                _ => text[$"AnalysisWarnings.{code}"]
            };
            expected = text.Format("AnalysisWarnings.WithDiagnostic", expected, id);
            if (loggingFailed) expected = text.Format("AnalysisWarnings.LoggingFailed", expected);
            var raw = await RenderAsync(result);
            var html = WebUtility.HtmlDecode(raw);
            Assert.Contains(expected, html);
            Assert.Contains(plain.Workspace, html);
            Assert.DoesNotContain(plain.Message, html);
            Assert.DoesNotContain("<workspace>", raw);
            Assert.Equal(original, JsonSerializer.Serialize(result));
            var restored = JsonSerializer.Deserialize<AnalysisQueryWarning>(JsonSerializer.Serialize(warning))!;
            Assert.Null(restored.Notice);
            Assert.Equal(plain.Message, AnalysisQueryWarningPresentation.Message(restored, text));
        }
        foreach (var notice in new AnalysisQueryNotice[]
        {
            new((AnalysisQueryNoticeCode)999),
            new(AnalysisQueryNoticeCode.QueryFailed, "<script>invalid-id</script>"),
            new(AnalysisQueryNoticeCode.AzureRequestFailed)
        })
        {
            var warning = new AnalysisQueryWarning("workspace", "private fallback") { Notice = notice };
            Assert.Equal(text["AnalysisWarnings.Unavailable"], AnalysisQueryWarningPresentation.Message(warning, text));
            var raw = await RenderAsync(Response(AnalysisQueryVisualizationTypes.Table) with { Warnings = [warning] });
            Assert.DoesNotContain("private fallback", raw);
            Assert.DoesNotContain("<script>", raw);
        }
        var lookalike = new AnalysisQueryWarning("own workspace",
            "Table result limited to 1000 rows for this workspace. More rows exist; narrow the filters or time range to inspect them.");
        Assert.Equal(lookalike.Message, AnalysisQueryWarningPresentation.Message(lookalike, text));
        Assert.Contains(lookalike.Message, WebUtility.HtmlDecode(await RenderAsync(
            Response(AnalysisQueryVisualizationTypes.Table) with { Warnings = [lookalike] })));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    private static AnalysisQueryResponse Response(string type)
    {
        var table = new AnalysisQueryTable("Workspace <original>",
            [new("Series", "string"), new("Value", "real"), new("Dimension", "string"), new("Unit", "string"),
                new("State", "string"), new("State__Status", "string")],
            [["New RCA <original row>", "1234.5", "Metric", "ms <unit>", "Stopped", "Error"]]);
        return new(Guid.NewGuid(), "Table view <query>", 1, "ms <unit>", Start, Start.AddHours(1), Start.AddHours(1), 9,
            [new("Table view <series>", "Metric", "Workspace <original>",
                [new(Start, 1234.5, "New RCA <point label>")]),
             new("Hidden <series>", "Hidden", "Workspace <original>", [new(Start, 2000, null)])],
            [new("Warning <workspace>", "New RCA <technical warning>")],
            type, type is AnalysisQueryVisualizationTypes.Table or AnalysisQueryVisualizationTypes.Stat ? [table] : null,
            AnalysisVisualization.Parse("""
                {"decimals":1,"maximum":2000,"thresholds":[{"value":0,"color":"green"},{"value":1000,"color":"red"}],
                 "columns":[{"name":"Metric","displayName":"Table view <configured dimension>"},
                            {"name":"Value","displayName":"Table view <configured column>"},
                            {"name":"Hidden","hidden":true},
                            {"name":"State","valueMappings":[{"value":"Stopped","text":"Stopped <mapped value>"}]}]}
                """), 15);
    }

    private static async Task<string> RenderAsync(AnalysisQueryResponse? result)
    {
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisResultView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Result"] = result }))).ToHtmlString());
    }

    private static string[] TechnicalAttributes(string html) =>
        Regex.Matches(html, "\\b(?:viewBox|points|x|y|width|height|fill|transform|style|aria-valuemin|aria-valuemax|aria-valuenow)=\"[^\"]*\"")
            .Select(match => match.Value).ToArray();

    private sealed class ResultActivator : IComponentActivator
    {
        public AnalysisResultView? Result { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AnalysisResultView result) Result = result;
            return component;
        }
    }

    internal sealed class DisplayCulture : IDisposable
    {
        private readonly CultureInfo ui = CultureInfo.CurrentUICulture;
        private readonly CultureInfo format = CultureInfo.CurrentCulture;
        public DisplayCulture(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.CurrentCulture = format;
        }
    }
}
