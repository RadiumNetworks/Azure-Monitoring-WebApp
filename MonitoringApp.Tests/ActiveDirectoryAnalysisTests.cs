using System.Text.Json;
using System.ClientModel.Primitives;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class ActiveDirectoryAnalysisTests
{
    [Fact]
    public void OffersExactRequestedTimeRangesAndAveragingIntervals()
    {
        Assert.Equal([60, 240, 480, 1440, 2880, 10080, 43200], AnalysisQuerySelections.TimeRanges);
        Assert.Equal([15, 30, 60], AnalysisQuerySelections.BinSizes);
        Assert.Equal(["NC", "SourceDSA", "DestDSA", "SourceDSASite", "DestDSASite"], AnalysisQuerySelections.Groupings.Keys);
    }

    [Theory]
    [InlineData(60, 15, "NC")]
    [InlineData(240, 30, "SourceDSA")]
    [InlineData(480, 60, "DestDSA")]
    [InlineData(1440, 15, "SourceDSASite")]
    [InlineData(2880, 30, "DestDSASite")]
    [InlineData(10080, 60, "NC")]
    [InlineData(43200, 15, "SourceDSA")]
    public void BuildsIndependentTypedSelectionsWithoutWorkbookPlaceholders(int minutes, int bin, string group)
    {
        var kql = AnalysisQueryRunner.BuildEffectiveKql("source", ["DC\"01"], bin, minutes, group);
        Assert.Contains($"let _TimePeriod = {minutes}m;", kql);
        Assert.Contains($"let _BinSize = {bin}m;", kql);
        Assert.Contains($"let _GroupBy = \"{group}\";", kql);
        Assert.Contains("DC\\u002201", kql);
        Assert.EndsWith("source", kql);
    }

    [Theory]
    [InlineData("SourceDSA\"; .show tables; //")]
    [InlineData("TimeGenerated")]
    [InlineData("")]
    public void RejectsUnknownOrInjectedGroupings(string group) =>
        Assert.Throws<InvalidOperationException>(() => AnalysisQueryRunner.BuildEffectiveKql("source", [], 15, 60, group));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1440)]
    public void RejectsUnsupportedOperatorIntervals(int bin) =>
        Assert.Throws<InvalidOperationException>(() => AnalysisQuerySelections.Validate(bin, "SourceDSA"));

    [Fact]
    public void LibraryCoversWorkbookExceptDeferredPatchQuery()
    {
        var queries = ActiveDirectoryAnalysisQueries.Create();
        Assert.Equal(38, queries.Count);
        Assert.Equal(queries.Count, queries.DistinctBy(query => query.Key).Count());
        foreach (var query in queries)
        {
            Assert.Empty(AnalysisQueryValidator.ValidateDefinition(query));
            Assert.Empty(AnalysisQueryValidator.ValidateKql(query.Kql, query.VisualizationType, query.Scope));
            if (query.Scope == AnalysisQueryScopes.SelectedSystems)
                Assert.Contains("Computer in~ (_SelectedSystems)", query.Kql);
            else
                Assert.Contains("Environment", query.Kql);
            Assert.Contains("ago(_TimePeriod)", query.Kql);
            Assert.DoesNotContain("{Computer}", query.Kql);
            Assert.DoesNotContain("{TimeAvg}", query.Kql);
            Assert.DoesNotContain("patchassessmentresources", query.Kql);
            Assert.Equal(43200, query.MaxTimeRangeMinutes);
            Assert.Equal(15, query.DefaultBinMinutes);
            Assert.Equal(AnalysisQueryStatuses.Draft, query.Status);
        }
        Assert.Contains(queries, query => query.VisualizationType == AnalysisQueryVisualizationTypes.Honeycomb);
        Assert.Contains(queries, query => query.Key == "ad-kerberos-rate" && query.Kql.Contains("Security System-Wide Statistics"));
        Assert.Contains(queries, query => query.Key == "ad-replication-errors" && query.Kql.Contains("_GroupBy"));
    }

    [Fact]
    public void NormalizesTimeseriesUsingSchemaAndUtcWithoutCultureDependentNumbers()
    {
        var table = new AnalysisQueryTable("Workspace A",
            [new("Timestamp", "datetime"), new("Series", "string"), new("Value", "real"), new("Dimension", "string")],
            [["2026-10-20T12:00:00+02:00", "DC01", "12.125", "CPU0"]]);
        var normalized = AnalysisQueryRunner.NormalizeResult(table, AnalysisQueryVisualizationTypes.LineChart);
        Assert.Null(normalized.Error);
        var row = Assert.Single(normalized.Rows);
        Assert.Equal(10, row.Timestamp.Hour);
        Assert.Equal(TimeSpan.Zero, row.Timestamp.Offset);
        Assert.Equal(12.125, row.Value);
        Assert.Equal("CPU0", row.Dimension);
    }

    [Fact]
    public void PreservesNullRealValuesFromAzureSdkForGpoStatistics()
    {
        var sdkResult = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
            """
            {"tables":[{"name":"PrimaryResult","columns":[
              {"name":"Series","type":"string"},{"name":"Dimension","type":"string"},{"name":"Value","type":"real"}],
              "rows":[["domain-a","LargeGPOs",0],["domain-b","LargePct",null],["domain-c","AvgLargeMiB",null]]}]}
            """))!;
        var normalized = AnalysisQueryRunner.NormalizeResult(
            sdkResult.Table, "Workspace", AnalysisQueryVisualizationTypes.Stat);
        Assert.Null(normalized.Error);
        var table = Assert.Single(normalized.Tables!);
        Assert.Equal("0", table.Rows[0][2]);
        Assert.Null(table.Rows[1][2]);
        Assert.Null(table.Rows[2][2]);
    }

    [Fact]
    public void ConvertsActualAzureSdkRowsIncludingDynamicValuesAndNulls()
    {
        var sdkResult = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
            """
            {"tables":[{"name":"PrimaryResult","columns":[
              {"name":"Timestamp","type":"datetime"},{"name":"Series","type":"string"},
              {"name":"Value","type":"real"},{"name":"Details","type":"dynamic"},{"name":"Label","type":"string"}],
              "rows":[["2026-10-20T10:00:00Z","DC01",12.125,"{\"key\":\"value\"}",null]]}]}
            """))!;
        var normalized = AnalysisQueryRunner.NormalizeResult(sdkResult.Table, "Workspace A", AnalysisQueryVisualizationTypes.LineChart);
        Assert.Null(normalized.Error);
        Assert.Equal(12.125, Assert.Single(normalized.Rows).Value);
        var table = Assert.Single(normalized.Tables!);
        Assert.Equal("Workspace A", table.Workspace);
        Assert.Equal("real", table.Columns[2].Type);
        Assert.Contains("\"key\":\"value\"", Assert.Single(table.Rows)[3]);
        Assert.Null(Assert.Single(table.Rows)[4]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("invalid")]
    public void MissingOrInvalidNumericValuesNeverBecomeZero(string? value)
    {
        var table = new AnalysisQueryTable("Workspace", [new("Series", "string"), new("Value", "real")], [["Site A -> Site B", value]]);
        Assert.NotNull(AnalysisQueryRunner.NormalizeResult(table, AnalysisQueryVisualizationTypes.Honeycomb).Error);
    }

    [Fact]
    public void RejectsWrongSchemasAndAcceptsTablesWithoutSyntheticNumericColumns()
    {
        var table = new AnalysisQueryTable("Workspace", [new("Computer", "string"), new("Result", "string")], [["DC01", null]]);
        Assert.Null(AnalysisQueryRunner.NormalizeResult(table, AnalysisQueryVisualizationTypes.Table).Error);
        Assert.Same(table, Assert.Single(AnalysisQueryRunner.NormalizeResult(table, AnalysisQueryVisualizationTypes.Table).Tables!));
        Assert.NotNull(AnalysisQueryRunner.NormalizeResult(table, AnalysisQueryVisualizationTypes.LineChart).Error);
        Assert.NotNull(AnalysisQueryRunner.NormalizeResult(table with { Rows = [["DC01"]] }, AnalysisQueryVisualizationTypes.Table).Error);
    }

    [Theory]
    [InlineData("Passed", "ok")]
    [InlineData("Running", "ok")]
    [InlineData("Warning", "warning")]
    [InlineData("Start Pending", "warning")]
    [InlineData("Failed", "error")]
    [InlineData("Stopped", "error")]
    [InlineData(null, "unknown")]
    [InlineData("Not run", "unknown")]
    public void StatusesAreExplicitAndUnknownIsNeverGreen(string? value, string expected) =>
        Assert.Equal(expected, AnalysisResultFormatting.Status(value));

    [Fact]
    public void LatencyColorsMatchWorkbookEndpointsAndClampOutsideRange()
    {
        Assert.Equal("hsl(120 65% 38%)", AnalysisResultFormatting.LatencyColor(0));
        Assert.Equal("hsl(60 65% 38%)", AnalysisResultFormatting.LatencyColor(1000));
        Assert.Equal("hsl(0 65% 38%)", AnalysisResultFormatting.LatencyColor(2000));
        Assert.Equal(AnalysisResultFormatting.LatencyColor(2000), AnalysisResultFormatting.LatencyColor(2500));
    }

    [Fact]
    public async Task RendersTableColumnsStatusIconsAndEscapesText()
    {
        var response = Response(AnalysisQueryVisualizationTypes.Table) with
        {
            Tables = [new("Workspace A",
                [new("Computer", "string"), new("Test", "string"), new("Test__Status", "string"), new("Status", "string")],
                [["<DC01>", "Connectivity", "Passed", "Warning"],
                 ["DC02", "Replication", "Failed", null]])]
        };
        var html = await RenderAsync(response);
        Assert.Contains("<table", html);
        Assert.Contains("AD Test", html);
        Assert.Contains("Workspace A", html);
        Assert.Contains("&lt;DC01&gt;", html);
        Assert.DoesNotContain("Test__Status", html);
        Assert.Contains("analysis-status-ok", html);
        Assert.Contains("analysis-status-warning", html);
        Assert.Contains("analysis-status-error", html);
        Assert.Contains("analysis-status-unknown", html);
        Assert.DoesNotContain("<svg", html);
    }

    [Fact]
    public async Task RendersHoneycombWithSiteDirectionMillisecondsAndWorkbookScale()
    {
        var response = Response(AnalysisQueryVisualizationTypes.Honeycomb);
        var html = await RenderAsync(response);
        Assert.Contains("<polygon", html);
        Assert.Contains("Site A -&gt; Site B", html);
        Assert.Contains("1000 ms", html);
        Assert.Contains("hsl(60 65% 38%)", html);
        Assert.Contains("2000 ms", html);
        Assert.Contains("data-analysis-honeycomb", html);
        Assert.Contains("analysis-honeycomb-viewport", html);
        Assert.Contains("aria-label=\"Honeycomb zoom\"", html);
        Assert.Contains("data-honeycomb-zoom=\"out\"", html);
        Assert.Contains("data-honeycomb-zoom=\"in\"", html);
        Assert.Contains("data-honeycomb-zoom=\"fit\"", html);
        Assert.Contains("transform=\"translate(", html);
        Assert.DoesNotContain("<polyline", html);
    }

    [Fact]
    public async Task HoneycombOmitsMissingTargetsButKeepsCompleteLongNamesAndNonDirectionalEntries()
    {
        var response = Response(AnalysisQueryVisualizationTypes.Honeycomb);
        var points = response.Series[0].Points;
        var html = await RenderAsync(response with
        {
            Series =
            [
                new("Missing target -> ", null, "Workspace", points),
                new("Site A -> Site B", null, "Workspace", points),
                new("Whitespace target -> \t ", null, "Workspace", points),
                new("Very long source site name -> Target Site", null, "Workspace", points),
                new("Standalone metric", null, "Workspace", points)
            ]
        });

        Assert.Equal(3, html.Split("<polygon", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Missing target", html);
        Assert.DoesNotContain("Whitespace target", html);
        Assert.Contains("Site A -&gt; Site B", html);
        Assert.Contains("Very long source site name -&gt; Target Site", html);
        Assert.Contains("Standalone metric", html);
        Assert.Contains("transform=\"translate(90 70)\"", html);
        Assert.Contains("transform=\"translate(195 131)\"", html);
        Assert.Contains("transform=\"translate(300 70)\"", html);
    }

    [Fact]
    public async Task HoneycombShowsExplicitMessageWhenOnlySourceOnlyEntriesRemain()
    {
        var response = Response(AnalysisQueryVisualizationTypes.Honeycomb);
        var html = await RenderAsync(response with
        {
            Series = [new("Site A -> ", null, "Workspace", response.Series[0].Points)]
        });

        Assert.DoesNotContain("<polygon", html);
        Assert.DoesNotContain("<svg", html);
        Assert.Contains("No honeycomb entries", html);
        Assert.Contains("No data is not an OK status", html);
    }

    [Fact]
    public async Task SourceOnlyEntriesRemainAvailableInOtherVisualizationsAndRawTables()
    {
        var response = Response(AnalysisQueryVisualizationTypes.BarChart);
        var series = new AnalysisQuerySeries("Source only -> ", null, "Workspace", response.Series[0].Points);
        var bars = await RenderAsync(response with { Series = [series] });
        Assert.Contains("Source only -&gt;", bars);

        var table = await RenderComponentAsync<AnalysisResultView>(new()
        {
            ["Result"] = response with { VisualizationType = AnalysisQueryVisualizationTypes.Honeycomb, Series = [series] },
            ["AllowTableView"] = true,
            ["ShowTable"] = true
        });
        Assert.Contains("<table", table);
        Assert.Contains("Source only -&gt;", table);
    }

    [Fact]
    public async Task RendersBarSummaryAndExplicitEmptyTable()
    {
        Assert.Contains("analysis-bar", await RenderAsync(Response(AnalysisQueryVisualizationTypes.BarChart)));
        var html = await RenderAsync(Response(AnalysisQueryVisualizationTypes.Table) with { Tables = [] });
        Assert.Contains("No data is not an OK status", html);
        Assert.DoesNotContain("analysis-status-ok", html);
    }

    [Fact]
    public async Task DropdownContainsAllRequestedChoicesAndSeparateReplicationGrouping()
    {
        var html = await RenderComponentAsync<AnalysisExecutionSelections>(new()
        {
            ["TimeRangeMinutes"] = 1440, ["BinMinutes"] = 15, ["ShowGrouping"] = true
        });
        foreach (var value in new[] { "1h", "4h", "8h", "24h", "2 days", "7 days", "30 days", "15 min", "30 min", "Naming Context", "Source DC", "Destination site" })
            Assert.Contains(value, html);
    }

    [Fact]
    public async Task RendersEntireGridSizeControlsWithSupportedLimits()
    {
        var html = await RenderComponentAsync<AnalysisGridControls>(new()
        {
            ["Document"] = new AnalysisDashboardDocument { Columns = 5, Rows = 16, RowHeight = 100 }
        });
        Assert.Contains("Dashboard grid size", html);
        Assert.Contains("Columns", html);
        Assert.Contains("Rows", html);
        Assert.Contains("Row height (px)", html);
        Assert.Contains("min=\"2\" max=\"6\" value=\"5\"", html);
        Assert.Contains("min=\"8\" max=\"120\" value=\"16\"", html);
        Assert.Contains("min=\"64\" max=\"132\" value=\"100\"", html);
    }

    [Fact]
    public void DashboardSelectionsSurviveSerializationUndoAndRedoAndOldDocuments()
    {
        var state = new AnalysisDashboardState();
        var widget = state.AddWidget(Guid.NewGuid(), "Replication", 60);
        state.UpdateWidget(widget.Id, item =>
        {
            item.TimeRangeMinutes = 43200;
            item.BinMinutes = 30;
            item.GroupBy = "DestDSASite";
        });
        var restored = JsonSerializer.Deserialize<AnalysisDashboardDocument>(JsonSerializer.Serialize(state.Document))!;
        var saved = Assert.Single(restored.Widgets);
        Assert.Equal(43200, saved.TimeRangeMinutes);
        Assert.Equal(30, saved.BinMinutes);
        Assert.Equal("DestDSASite", saved.GroupBy);
        state.Undo();
        Assert.Null(Assert.Single(state.Document.Widgets).BinMinutes);
        state.Redo();
        Assert.Equal("DestDSASite", Assert.Single(state.Document.Widgets).GroupBy);
        var old = JsonSerializer.Deserialize<AnalysisDashboardWidget>("{}")!;
        Assert.Null(old.BinMinutes);
        Assert.Equal("SourceDSA", old.GroupBy);
    }

    [Fact]
    public async Task InstallsDraftsIdempotentlyWithoutOverwritingExistingQueries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await using (var context = factory.CreateDbContext())
        {
            var names = new[] { "AnalysisQueryDefinitions", "AnalysisQueryWorkspaces", "AnalysisQueryVersions" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
        }
        var options = new CrossTenantLogAnalyticsOptions { Workspaces = [new() { Key = "test" }] };
        var registry = new AnalysisQueryRegistry(factory, options,
            new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options)));
        Assert.Equal(38, await registry.InstallActiveDirectoryLibraryAsync("admin"));
        var first = (await registry.ListAsync()).First();
        first.Name = "Customized";
        first.Kql += "\n| take 1";
        first.VisualizationType = AnalysisQueryVisualizationTypes.Table;
        first.DefaultTimeRangeMinutes = 60;
        first.DefaultBinMinutes = 30;
        first.VisualizationJson = """{"color":"purple","columns":[{"name":"Computer","width":220}]}""";
        await registry.SaveDraftAsync(first, "admin");
        Assert.Equal(0, await registry.InstallActiveDirectoryLibraryAsync("admin"));
        var saved = await registry.ListAsync();
        Assert.Equal(38, saved.Count);
        var customized = Assert.Single(saved, query => query.Id == first.Id);
        Assert.Equal("Customized", customized.Name);
        Assert.Equal(first.Kql, customized.Kql);
        Assert.Equal(AnalysisQueryVisualizationTypes.Table, customized.VisualizationType);
        Assert.Equal(60, customized.DefaultTimeRangeMinutes);
        Assert.Equal(30, customized.DefaultBinMinutes);
        Assert.Equal(first.VisualizationJson, customized.VisualizationJson);
        Assert.All(saved, query =>
        {
            Assert.Null(query.PublishedVersionId);
            Assert.Equal(AnalysisQueryStatuses.Draft, query.Status);
            Assert.Equal(AnalysisQueryValidationStatuses.NotValidated, Assert.Single(query.Workspaces).LastValidationStatus);
        });
    }

    [Fact]
    public void PublishedPresentationUsesVersionedSnapshotAndOldRequestsKeepDefaults()
    {
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.VisualizationType == AnalysisQueryVisualizationTypes.Honeycomb);
        var snapshot = AnalysisQueryRegistry.CreateSnapshot(query);
        var version = new AnalysisQueryVersion { Id = Guid.NewGuid(), Version = 1, SnapshotJson = JsonSerializer.Serialize(snapshot) };
        query.PublishedVersionId = version.Id;
        query.Versions.Add(version);
        query.VisualizationType = AnalysisQueryVisualizationTypes.Table;
        Assert.Equal(AnalysisQueryVisualizationTypes.Honeycomb, AnalysisQueryRegistry.CreatePublishedDefinition(query).VisualizationType);
        var request = JsonSerializer.Deserialize<AnalysisQueryRequest>(
            """{"queryDefinitionId":"00000000-0000-0000-0000-000000000001","systems":[],"timeRangeMinutes":60}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(request.BinMinutes);
        Assert.Equal("SourceDSA", request.GroupBy);
    }

    private static AnalysisQueryResponse Response(string visualization)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "AD Test", 1, "ms", now.AddHours(-1), now, now, 1,
            [new("Site A -> Site B", null, "Workspace A", [new(now, 1000, null)])], [], visualization);
    }

    private static Task<string> RenderAsync(AnalysisQueryResponse response) =>
        RenderComponentAsync<AnalysisResultView>(new() { ["Result"] = response });

    private static async Task<string> RenderComponentAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Library installation must not access credentials.");
    }
}
