using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class AnalysisVisualizationTests
{
    [Fact]
    public void EditorFormattingIndentsNestedJsonWithoutDroppingNullMappingsOrAddingDefaults()
    {
        const string compact = """{"columns":[{"name":"State","valueMappings":[{"value":null,"text":"Unknown","color":"gray"}]}]}""";
        var formatted = AnalysisVisualization.FormatForEditing(compact);
        Assert.Contains("\n  \"columns\": [", formatted.Replace("\r\n", "\n"));
        Assert.Contains("\n      \"valueMappings\": [", formatted.Replace("\r\n", "\n"));
        Assert.Contains("\"value\": null", formatted);
        Assert.DoesNotContain("\"wrapText\"", formatted);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(compact), System.Text.Json.Nodes.JsonNode.Parse(formatted)));
        Assert.Equal(formatted, AnalysisVisualization.FormatForEditing(formatted));
    }

    [Fact]
    public void OpeningRegistryEditorFormatsOnlyItsEditableCopy()
    {
        const string compact = """{"thresholds":[{"value":0,"color":"green"}]}""";
        var (page, draft) = OpenRegistryEditor(compact);
        Assert.Contains("\n  \"thresholds\": [", draft.VisualizationJson.Replace("\r\n", "\n"));
        Assert.Null(typeof(AdminAnalysisQueries).GetField("errorMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
    }

    [Fact]
    public void InvalidJsonRemainsEditableAndShowsAnExplicitFormattingError()
    {
        var (page, draft) = OpenRegistryEditor("{");
        Assert.Equal("{", draft.VisualizationJson);
        var error = Assert.IsType<string>(typeof(AdminAnalysisQueries)
            .GetField("errorMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
        Assert.Contains("original text is available for editing", error);
    }

    [Fact]
    public void FormattingDoesNotMakeAnOversizedDocumentUneditableWithoutWarning()
    {
        var compact = "{\"nullText\":\"" + new string('x', 65520) + "\"}";
        Assert.True(compact.Length <= 65536);
        Assert.Throws<InvalidOperationException>(() => AnalysisVisualization.FormatForEditing(compact));
        var (page, draft) = OpenRegistryEditor(compact);
        Assert.Equal(compact, draft.VisualizationJson);
        Assert.Contains("editor limit", Assert.IsType<string>(typeof(AdminAnalysisQueries)
            .GetField("errorMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)));
    }

    private static (AdminAnalysisQueries Page, AnalysisQueryDefinition Draft) OpenRegistryEditor(string json)
    {
        var page = new AdminAnalysisQueries();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AdminAnalysisQueries).GetProperty("Options", flags)!.SetValue(page, new CrossTenantLogAnalyticsOptions());
        var original = new AnalysisQueryDefinition { VisualizationJson = json };
        typeof(AdminAnalysisQueries).GetMethod("BeginEdit", flags)!.Invoke(page, [original]);
        Assert.Equal(json, original.VisualizationJson);
        return (page, Assert.IsType<AnalysisQueryDefinition>(typeof(AdminAnalysisQueries).GetField("editing", flags)!.GetValue(page)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"colours":[]}""")]
    [InlineData("""{"color":"red;background:url(https://example.com)"}""")]
    [InlineData("""{"nullColor":null}""")]
    [InlineData("""{"thresholds":null}""")]
    [InlineData("""{"columns":[null]}""")]
    [InlineData("""{"columns":[{}]}""")]
    [InlineData("""{"columns":[{"name":"X"},{"name":"X"}]}""")]
    [InlineData("""{"columns":[{"name":"X","width":49}]}""")]
    [InlineData("""{"columns":[{"name":"X","width":1001}]}""")]
    [InlineData("""{"columns":[{"name":"X","breakAtFirstDot":"true"}]}""")]
    [InlineData("""{"columns":[{"name":"X","cellDisplay":"gradient"}]}""")]
    [InlineData("""{"decimals":7}""")]
    [InlineData("""{"statTitleFontSize":7}""")]
    [InlineData("""{"statTitleFontSize":73}""")]
    [InlineData("""{"statValueFontSize":7}""")]
    [InlineData("""{"statValueFontSize":73}""")]
    [InlineData("""{"statTitleFontSize":12.5}""")]
    [InlineData("""{"statValueFontSize":"18px"}""")]
    [InlineData("""{"cellDisplay":"HTML"}""")]
    [InlineData("""{"minimum":100,"maximum":100}""")]
    [InlineData("""{"maximum":-1}""")]
    [InlineData("""{"thresholds":[{"color":"red"}]}""")]
    [InlineData("""{"thresholds":[{"value":0}]}""")]
    [InlineData("""{"thresholds":[{"value":10,"color":"red"},{"value":0,"color":"green"}]}""")]
    [InlineData("""{"thresholds":[{"value":0,"color":"red"},{"value":0,"color":"green"}]}""")]
    [InlineData("""{"valueMappings":[{"value":"A"},{"value":"a"}]}""")]
    [InlineData("""{"valueMappings":[{"value":null},{"value":null}]}""")]
    [InlineData("""{"columns":[{"name":"Value","unknown":true}]}""")]
    public void RejectsInvalidPresentationRatherThanSilentlyFallingBack(string json) =>
        Assert.Throws<InvalidOperationException>(() => AnalysisVisualization.Parse(json));

    [Fact]
    public void EnforcesConfigurationSizeAndFiniteNumbers()
    {
        Assert.Throws<InvalidOperationException>(() => AnalysisVisualization.Parse(new string(' ', 65537)));
        Assert.Throws<InvalidOperationException>(() =>
            AnalysisVisualization.Validate(new() { Maximum = double.PositiveInfinity }));
        Assert.Throws<InvalidOperationException>(() =>
            AnalysisVisualization.Validate(new() { Thresholds = [new(double.NaN, "red")] }));
        Assert.Throws<InvalidOperationException>(() =>
            AnalysisVisualization.Validate(new() { Thresholds = Enumerable.Range(0, 51).Select(index => new AnalysisColorThreshold(index, "red")).ToList() }));
    }

    [Fact]
    public void AppliesInclusiveThresholdsMappingsNullHandlingAndOverrides()
    {
        var options = AnalysisVisualization.Parse("""
            {
              "color":"blue","thresholds":[{"value":0,"color":"green"},{"value":10,"color":"red"}],
              "valueMappings":[{"value":"Stopped","text":"Not running","color":"orange"}],
              "columns":[{"name":"Other","thresholds":[{"value":0,"color":"purple"}]},
                {"name":"Missing","valueMappings":[{"value":null,"text":"No telemetry","color":"yellow"}]}]
            }
            """);
        Assert.Equal("#245b91", AnalysisVisualization.Color(options, "-1", "Value"));
        Assert.Equal("#176b40", AnalysisVisualization.Color(options, "0", "Value"));
        Assert.Equal("#176b40", AnalysisVisualization.Color(options, "9.999", "Value"));
        Assert.Equal("#a12424", AnalysisVisualization.Color(options, "10", "Value"));
        Assert.Equal("#a44912", AnalysisVisualization.Color(options, "stopped", "Value"));
        Assert.Equal("Not running", AnalysisVisualization.Text(options, "STOPPED", "Value"));
        Assert.Equal("#7040a0", AnalysisVisualization.Color(options, "10", "Other"));
        Assert.Equal("#53616d", AnalysisVisualization.Color(options, null, "Value"));
        Assert.Equal("Unknown", AnalysisVisualization.Text(options, null, "Value"));
        Assert.Equal("#805500", AnalysisVisualization.Color(options, null, "Missing"));
        Assert.Equal("No telemetry", AnalysisVisualization.Text(options, null, "Missing"));
        Assert.Equal("#ffffff", AnalysisVisualization.Foreground("#176b40"));
        Assert.Equal("#17211e", AnalysisVisualization.Foreground("#ffff00"));
    }

    [Theory]
    [InlineData("SERVER.domain.example", "SERVER\ndomain.example")]
    [InlineData("SERVER", "SERVER")]
    [InlineData("", "")]
    [InlineData(null, "Unknown")]
    [InlineData("SERVER.", "SERVER\n")]
    public void FirstDotBreakOnlyChangesConfiguredTableText(string? value, string expected)
    {
        var options = AnalysisVisualization.Parse("""
            {"columns":[{"name":"Holder","breakAtFirstDot":true}]}
            """);
        Assert.Equal(expected, AnalysisVisualization.TableText(options, value, "Holder"));
        Assert.Equal(value ?? "Unknown", AnalysisVisualization.Text(options, value, "Holder"));
        Assert.Equal(value ?? "Unknown", AnalysisVisualization.TableText(options, value, "Domain"));
        Assert.True(Assert.Single(AnalysisVisualization.Parse(AnalysisVisualization.Serialize(options)).Columns).BreakAtFirstDot);
        options.Columns[0].BreakAtFirstDot = false;
        Assert.Equal(value ?? "Unknown", AnalysisVisualization.TableText(options, value, "Holder"));
    }

    [Theory]
    [InlineData(true, "white-space:pre-line;")]
    [InlineData(false, "white-space:pre;")]
    public async Task FirstDotBreakIsRenderedSafelyAndRespectsAutomaticWrapping(bool wrap, string expectedStyle)
    {
        var options = new AnalysisVisualizationOptions
        {
            WrapText = wrap,
            Columns = [new() { Name = "Holder", BreakAtFirstDot = true, Color = "blue", CellDisplay = "ColorText" }]
        };
        var result = Response(AnalysisQueryVisualizationTypes.Table) with
        {
            Visualization = options,
            Tables = [new("A", [new("Holder", "string"), new("Domain", "string")],
                [["SERVER.<domain>.example", "domain.example"]])]
        };
        var html = await RenderAsync(result);
        Assert.Contains("SERVER\n<domain>.example", System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("<domain>", html);
        Assert.Contains(expectedStyle, html);
        Assert.Contains("color:#245b91;", html);
        Assert.Contains("domain.example", html);
        Assert.Equal("SERVER.<domain>.example", result.Tables[0].Rows[0][0]);
    }

    [Fact]
    public void FirstDotBreakPreservesMappingsNullPlaceholdersAndPublishedSettings()
    {
        var definition = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-fsmo-roles");
        var options = AnalysisVisualization.Parse(definition.VisualizationJson);
        var holder = AnalysisVisualization.Column(options, "PDCEmulator")!;
        Assert.True(holder.BreakAtFirstDot);
        Assert.False(AnalysisVisualization.Column(options, "DNSRoot")!.BreakAtFirstDot);
        holder.ValueMappings.Add(new("raw", "Mapped.domain.example", "red"));
        holder.NullText = "No telemetry. Retry later.";
        Assert.Equal("Mapped\ndomain.example", AnalysisVisualization.TableText(options, "raw", "PDCEmulator"));
        Assert.Equal("#a12424", AnalysisVisualization.Color(options, "raw", "PDCEmulator"));
        Assert.Equal(holder.NullText, AnalysisVisualization.TableText(options, null, "PDCEmulator"));
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(), Version = 1,
            SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(definition))
        };
        definition.PublishedVersionId = version.Id;
        definition.Versions.Add(version);
        definition.VisualizationJson = """{"columns":[{"name":"PDCEmulator","breakAtFirstDot":false}]}""";
        var published = AnalysisVisualization.Parse(AnalysisQueryRegistry.CreatePublishedDefinition(definition).VisualizationJson);
        Assert.True(AnalysisVisualization.Column(published, "PDCEmulator")!.BreakAtFirstDot);
    }

    [Fact]
    public void FormatsInvariantInputWithCurrentCultureAndUtcDates()
    {
        var options = AnalysisVisualization.Parse("""
            {"decimals":2,"columns":[{"name":"Timestamp","unit":"datetime"}]}
            """);
        Assert.Equal(12.345.ToString("F2", CultureInfo.CurrentCulture),
            AnalysisVisualization.Text(options, "12.345", "Value"));
        Assert.Equal("2026-10-21 10:00:00 UTC",
            AnalysisVisualization.Text(options, "2026-10-21T12:00:00+02:00", "Timestamp"));
        Assert.Null(AnalysisVisualization.Unit(options, "Timestamp"));
    }

    [Theory]
    [InlineData(8, 72)]
    [InlineData(12, 18)]
    [InlineData(72, 8)]
    public async Task StatFontsAreValidatedSerializedAndRenderedIndependently(int titleSize, int valueSize)
    {
        var options = AnalysisVisualization.Parse($$"""{"statTitleFontSize":{{titleSize}},"statValueFontSize":{{valueSize}},"color":"purple"}""");
        var restored = AnalysisVisualization.Parse(AnalysisVisualization.Serialize(options));
        Assert.Equal(titleSize, restored.StatTitleFontSize);
        Assert.Equal(valueSize, restored.StatValueFontSize);
        var result = Response(AnalysisQueryVisualizationTypes.Stat) with
        {
            Visualization = restored,
            Tables = [new("A", [new("Series", "string"), new("Value", "real")], [["Domain", "12"]])]
        };
        var html = await RenderAsync(result);
        Assert.Contains($"<strong style=\"font-size:{titleSize}px;\"", html);
        Assert.Contains($"color:#7040a0;font-size:{valueSize}px;--analysis-stat-unit-font-size:0.5em;", html);
    }

    [Fact]
    public async Task StatFontsKeepLegacyDefaultsAndCanBeSetIndependently()
    {
        var result = Response(AnalysisQueryVisualizationTypes.Stat) with
        {
            Tables = [new("A", [new("Series", "string"), new("Value", "real")], [["Domain", "0"]])]
        };
        foreach (var json in new[] { "{}", """{"statTitleFontSize":null,"statValueFontSize":null}""" })
        {
            var options = AnalysisVisualization.Parse(json);
            Assert.Null(options.StatTitleFontSize);
            Assert.Null(options.StatValueFontSize);
            var html = await RenderAsync(result with { Visualization = options });
            Assert.DoesNotContain("font-size:", html);
        }
        var titleOnly = await RenderAsync(result with { Visualization = AnalysisVisualization.Parse("""{"statTitleFontSize":12}""") });
        Assert.Contains("<strong style=\"font-size:12px;\"", titleOnly);
        Assert.DoesNotContain("--analysis-stat-unit-font-size:", titleOnly);
        var valueOnly = await RenderAsync(result with { Visualization = AnalysisVisualization.Parse("""{"statValueFontSize":18}""") });
        Assert.DoesNotContain("<strong style=", valueOnly);
        Assert.Contains("font-size:18px;", valueOnly);
    }

    [Fact]
    public void PublishedStatFontSizesRemainIndependentOfDraftChanges()
    {
        var definition = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-gpo-statistics");
        definition.VisualizationJson = """{"statTitleFontSize":12,"statValueFontSize":18}""";
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(), Version = 1,
            SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(definition))
        };
        definition.PublishedVersionId = version.Id;
        definition.Versions.Add(version);
        definition.VisualizationJson = """{"statTitleFontSize":20,"statValueFontSize":30}""";
        var published = AnalysisVisualization.Parse(AnalysisQueryRegistry.CreatePublishedDefinition(definition).VisualizationJson);
        Assert.Equal(12, published.StatTitleFontSize);
        Assert.Equal(18, published.StatValueFontSize);
    }

    [Fact]
    public void ResponseSerializationRetainsPresentationAndEffectiveBinAndAcceptsLegacyResponses()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var response = Response(AnalysisQueryVisualizationTypes.StatusHistory) with
        {
            BinMinutes = 30,
            Visualization = AnalysisVisualization.Parse("""{"color":"purple","columns":[{"name":"Value","decimals":2}]}""")
        };
        var restored = JsonSerializer.Deserialize<AnalysisQueryResponse>(JsonSerializer.Serialize(response, jsonOptions), jsonOptions)!;
        Assert.Equal(30, restored.BinMinutes);
        Assert.Equal("purple", restored.Visualization!.Color);
        Assert.Equal(2, Assert.Single(restored.Visualization.Columns).Decimals);
        var legacy = JsonSerializer.SerializeToNode(response, jsonOptions)!.AsObject();
        legacy.Remove("visualization");
        legacy.Remove("binMinutes");
        var old = legacy.Deserialize<AnalysisQueryResponse>(jsonOptions)!;
        Assert.Null(old.Visualization);
        Assert.Null(old.BinMinutes);
    }

    [Fact]
    public void PublishedSnapshotIsIndependentOfDraftAndOlderSnapshotsKeepDefaults()
    {
        var definition = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-storage-gauge");
        var original = AnalysisQueryRegistry.CreateSnapshot(definition);
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(), Version = 1,
            SnapshotJson = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        definition.PublishedVersionId = version.Id;
        definition.Versions.Add(version);
        definition.VisualizationJson = """{"color":"purple"}""";
        var published = AnalysisQueryRegistry.CreatePublishedDefinition(definition);
        var options = AnalysisVisualization.Parse(published.VisualizationJson);
        Assert.Equal(100, options.Maximum);
        Assert.Equal("#701b1b", AnalysisVisualization.Color(options, "0", "C:"));
        Assert.Null(options.Color);

        var legacy = JsonSerializer.SerializeToNode(original, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        legacy.Remove("visualization");
        version.SnapshotJson = legacy.ToJsonString();
        Assert.Empty(AnalysisVisualization.Parse(AnalysisQueryRegistry.CreatePublishedDefinition(definition).VisualizationJson).Thresholds);
    }

    [Fact]
    public void MigrationAddsDefaultWithoutPendingModelChanges()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;").Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261020170000_AllowSharedDashboardNames", "20261021140000_AddAnalysisVisualizationOptions");
        Assert.Contains("ADD [VisualizationJson] nvarchar(max) NOT NULL DEFAULT N'{}'", script);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void ScopeMigrationKeepsExistingQueriesSystemScoped()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;").Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261021140000_AddAnalysisVisualizationOptions", "20261021143000_AddAnalysisQueryScope");
        Assert.Contains("ADD [Scope] nvarchar(32) NOT NULL DEFAULT N'SelectedSystems'", script);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task WorkspaceScopeIsDisclosedForStatsAndLegacyResultsHaveNoWorkspaceWarning()
    {
        var response = Response(AnalysisQueryVisualizationTypes.Stat) with
        {
            Scope = AnalysisQueryScopes.Workspace,
            Tables = [new("Workspace A", [new("Series", "string"), new("Value", "real")], [["domain.example", "2"]])]
        };
        var html = await RenderAsync(response);
        Assert.Contains("Workspace-wide results", html);
        Assert.Contains("not the returned systems/domains", html);
        Assert.Contains("domain.example", html);
        Assert.DoesNotContain("Workspace-wide results", await RenderAsync(response with { Scope = AnalysisQueryScopes.SelectedSystems }));
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(response, jsonOptions)!.AsObject();
        Assert.Equal(AnalysisQueryScopes.Workspace, json.Deserialize<AnalysisQueryResponse>(jsonOptions)!.Scope);
        json.Remove("scope");
        Assert.Equal(AnalysisQueryScopes.SelectedSystems, json.Deserialize<AnalysisQueryResponse>(jsonOptions)!.Scope);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("2.5", true)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    public void StatsPreserveUnknownInsteadOfConvertingItToZero(string? value, bool valid)
    {
        var table = new AnalysisQueryTable("A", [new("Series", "string"), new("Value", "real")], [["DC01", value]]);
        var result = AnalysisQueryRunner.NormalizeResult(table, AnalysisQueryVisualizationTypes.Stat);
        Assert.Equal(valid, result.Error is null);
        Assert.Empty(result.Rows);
        if (valid)
            Assert.Equal(value, Assert.Single(Assert.Single(result.Tables!).Rows)[1]);
    }

    [Fact]
    public void StatusHistoryRequiresTypedTimestampsAndBinParameter()
    {
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-gpo-history");
        Assert.NotEmpty(AnalysisQueryValidator.ValidateKql(query.Kql.Replace("_BinSize", "15m"), query.VisualizationType, query.Scope));
        var table = new AnalysisQueryTable("A", [new("Series", "string"), new("Value", "real")], [["DC01", "0"]]);
        Assert.NotNull(AnalysisQueryRunner.NormalizeResult(table, AnalysisQueryVisualizationTypes.StatusHistory).Error);
        var timestamped = table with
        {
            Columns = [new("Timestamp", "datetime"), .. table.Columns],
            Rows = [["2026-10-21T10:00:00Z", "DC01", "0"]]
        };
        Assert.Null(AnalysisQueryRunner.NormalizeResult(timestamped, AnalysisQueryVisualizationTypes.StatusHistory).Error);
    }

    [Theory]
    [InlineData(AnalysisQueryVisualizationTypes.Table, false)]
    [InlineData(AnalysisQueryVisualizationTypes.LineChart, true)]
    public async Task TableHeadersPreserveJsonLineBreaksWithoutRenderingHtml(string visualizationType, bool showTable)
    {
        var result = Response(visualizationType) with
        {
            Series = [new("Count", null, "A", [new(Start, 1, null)])],
            Visualization = AnalysisVisualization.Parse("""
                {"wrapText":false,"columns":[
                  {"name":"Count","displayName":"Replication\nfailures <count>","width":160}
                ]}
                """),
            Tables = [new("A", [new("Count", "long")], [["1"]])]
        };
        var html = await RenderAsync(result, table: showTable);
        Assert.Contains("Replication\nfailures <count></th>", System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("<count>", html);
        Assert.Contains("min-width:160px;max-width:160px;", html);
    }

    [Fact]
    public async Task TablesUseConfiguredOrderVisibilityMappingsWidthsAndEscapedLabels()
    {
        var result = Response(AnalysisQueryVisualizationTypes.Table) with
        {
            Visualization = AnalysisVisualization.Parse("""
                {"columns":[
                  {"name":"Count","displayName":"Failures <count>","width":150,"unit":"events","cellDisplay":"ColorBackground","thresholds":[{"value":0,"color":"green"},{"value":1,"color":"red"}]},
                  {"name":"State","valueMappings":[{"value":"Running","text":"<Live>","color":"green"}],"cellDisplay":"ColorText"},
                  {"name":"Internal","hidden":true}]}
                """),
            Tables = [new("A", [new("Internal", "string"), new("Computer", "string"), new("State", "string"), new("State__Status", "string"), new("Count", "long")],
                [["secret", "<DC01>", "Running", "Passed", "1"], ["secret", "DC02", null, null, null]])]
        };
        var html = await RenderAsync(result);
        Assert.DoesNotContain("secret", html);
        Assert.DoesNotContain("<th scope=\"col\">Internal", html);
        Assert.DoesNotContain("State__Status", html);
        Assert.Contains("Failures &lt;count&gt;", html);
        Assert.Contains("min-width:150px;max-width:150px;", html);
        Assert.Contains("background-color:#a12424;color:#ffffff;", html);
        Assert.Contains("background-color:#53616d", html);
        Assert.Contains("&lt;Live&gt;", html);
        Assert.Contains("&lt;DC01&gt;", html);
        Assert.Contains("analysis-status-ok", html);
        Assert.True(html.IndexOf("Failures &lt;count&gt;", StringComparison.Ordinal) < html.IndexOf(">State</th>", StringComparison.Ordinal));
        Assert.Equal(1, Count(html, "class=\"analysis-result-table\""));
    }

    [Fact]
    public async Task GaugeClampsOnlyItsLengthAndShowsActualValueAndThresholdColor()
    {
        var result = Response(AnalysisQueryVisualizationTypes.BarGauge) with
        {
            Series = [new("DC01", null, "A", [new(Start, 150, null)])],
            Visualization = AnalysisVisualization.Parse("""{"minimum":0,"maximum":100,"decimals":1,"thresholds":[{"value":0,"color":"green"},{"value":90,"color":"red"}]}""")
        };
        var html = await RenderAsync(result);
        Assert.Contains("analysis-gauge-track", html);
        Assert.Contains("role=\"meter\"", html);
        Assert.Contains("aria-valuenow=\"100\"", html);
        Assert.Contains("width:100%;background-color:#a12424;", html);
        Assert.Contains(150d.ToString("F1", CultureInfo.CurrentCulture), html);
    }

    [Theory]
    [InlineData(AnalysisQueryVisualizationTypes.LineChart)]
    [InlineData(AnalysisQueryVisualizationTypes.Honeycomb)]
    [InlineData(AnalysisQueryVisualizationTypes.BarChart)]
    [InlineData(AnalysisQueryVisualizationTypes.BarGauge)]
    public async Task ChartsHonorMetricNamesFixedColorsAndHiddenSeries(string type)
    {
        var result = Response(type) with
        {
            Visualization = AnalysisVisualization.Parse("""
                {"columns":[{"name":"Metric","displayName":"Visible metric","color":"purple"},{"name":"Hidden","hidden":true}]}
                """),
            Series = [
                new("DC01", "Metric", "A", [new(Start.AddMinutes(15), 12, null)]),
                new("Secret DC", "Hidden", "A", [new(Start.AddMinutes(15), 50, null)])]
        };
        var html = await RenderAsync(result);
        Assert.Contains("Visible metric", html);
        Assert.Contains("#7040a0", html);
        Assert.DoesNotContain("Secret DC", html);
    }

    [Fact]
    public async Task StatsShowUnknownSeparatelyFromZeroAndDoNotMergeWorkspaces()
    {
        var result = Response(AnalysisQueryVisualizationTypes.Stat) with
        {
            Visualization = AnalysisVisualization.Parse("""{"thresholds":[{"value":0,"color":"green"}]}"""),
            Tables = [
                new("Workspace A", [new("Series", "string"), new("Value", "real")], [["DC01", null]]),
                new("Workspace B", [new("Series", "string"), new("Value", "real")], [["DC01", "0"]])]
        };
        var html = await RenderAsync(result);
        Assert.Equal(2, Count(html, "class=\"analysis-stat\""));
        Assert.Contains("Unknown", html);
        Assert.Contains("color:#53616d;", html);
        Assert.Contains("color:#176b40;", html);
        Assert.Contains("Workspace A", html);
        Assert.Contains("Workspace B", html);
        Assert.DoesNotContain("no time-series rows", html);
        var tableHtml = await RenderAsync(result, table: true);
        Assert.Contains("Query result: Workspace A", tableHtml);
        Assert.DoesNotContain("class=\"analysis-stat\"", tableHtml);
        var empty = await RenderAsync(result with { Tables = [] });
        Assert.Contains("No data is not an OK status", empty);
    }

    [Fact]
    public async Task StatusHistoryDoesNotStretchSparseObservationsAcrossGaps()
    {
        var result = Response(AnalysisQueryVisualizationTypes.StatusHistory) with
        {
            BinMinutes = 15,
            Visualization = AnalysisVisualization.Parse("""{"thresholds":[{"value":0,"color":"green"},{"value":1,"color":"red"}]}"""),
            Series = [new("DC01", null, "Workspace A", [new(Start, 0, null), new(Start.AddMinutes(45), 1, null)])]
        };
        var html = await RenderAsync(result);
        Assert.Contains("analysis-status-history", html);
        Assert.Contains("Gaps mean no observations", html);
        Assert.Equal(2, Count(html, "width=\"170\""));
        Assert.Contains("x=\"310\"", html);
        Assert.Contains("x=\"820\"", html);
        Assert.Contains("fill=\"#176b40\"", html);
        Assert.Contains("fill=\"#a12424\"", html);
        Assert.DoesNotContain("width=\"510\"", html);
        Assert.Contains("requires the execution bin interval", await RenderAsync(result with { BinMinutes = null }));
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static AnalysisQueryResponse Response(string type) =>
        new(Guid.NewGuid(), "Visualization test", 1, "%", Start, Start.AddHours(1), Start.AddHours(1), 1, [], [], type);

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    private static async Task<string> RenderAsync(AnalysisQueryResponse response, bool table = false)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisResultView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Result"] = response, ["AllowTableView"] = table, ["ShowTable"] = table
                }))).ToHtmlString());
    }
}
