using Azure;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.ClientModel.Primitives;
using System.Reflection;
using MonitoringApp.Components.Pages;
using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisQueryFailureTests
{
    private const string Subscription = "11111111-1111-1111-1111-111111111111";
    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(100)]
    public async Task NetlogonQuerySendsEverySelectedSystemAndPreservesReturnedRows(int count)
    {
        var systems = Enumerable.Range(1, count).Select(index => new ComputerInventoryEntry
        {
            SubscriptionId = Subscription, Computer = $"DC{index:000}"
        }).ToArray();
        var client = new StubQuery((_, _) => Task.FromResult(Result(systems.Select(system => system.Computer).ToArray())));
        var (runner, options, _) = CreateRunner(client);
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-netlogon-errors");
        query.Workspaces =
        [
            new() { WorkspaceKey = "production", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded }
        ];
        var response = await runner.ExecuteAsync(query, systems, 1440);

        Assert.Empty(response.Warnings);
        Assert.Equal(count, Assert.Single(response.Tables!).Rows.Count);
        var preview = Assert.Single(runner.CreateExecutionPreviews(query, systems, 1440));
        Assert.Equal(count, preview.Systems.Count);
        Assert.Equal(preview.EffectiveKql, client.Kql);
        Assert.Contains("let _TimePeriod = 1440m;", client.Kql);
        foreach (var system in systems)
            Assert.Contains($"\"{system.Computer}\"", client.Kql);
        Assert.Equal(options.ApplicationClientId, client.ApplicationClientId);
        Assert.Equal(1440, client.Minutes);
    }

    [Fact]
    public async Task AzureFailureShowsStatusAndCodeAndLogsBoundedDetailsWithMatchingDiagnosticId()
    {
        var exception = new RequestFailedException(429, "Detailed Azure throttling reason", "TooManyRequests", null);
        var client = new StubQuery((_, _) => Task.FromException<LogsQueryResult>(exception));
        var (runner, options, logger) = CreateRunner(client);
        var result = await runner.TryQueryAsync(options.Workspaces[0], "Event",
            Enumerable.Range(1, 14).Select(index => $"DC{index}").ToArray(), 1440, 15, default);

        Assert.NotNull(result.Error);
        Assert.Contains("Azure HTTP 429 (TooManyRequests)", result.Error);
        Assert.DoesNotContain("RequestFailedException", result.Error);
        var diagnosticId = result.Error.Split("Diagnostic ID: ")[1].Split('.')[0];
        Assert.True(Guid.TryParseExact(diagnosticId, "N", out _));
        var entry = Assert.Single(logger.Entries);
        Assert.Contains(diagnosticId, entry);
        Assert.Contains("Workspace=production", entry);
        Assert.Contains("Systems=14", entry);
        Assert.Contains("Minutes=1440", entry);
        Assert.Contains("RequestFailedException", entry);
        Assert.Contains("Detailed Azure throttling reason", entry);
        Assert.Null(Assert.Single(logger.Exceptions));
        Assert.DoesNotContain("not-a-real-secret", entry);
        Assert.Empty(result.Rows);
        Assert.Equal(AnalysisQueryNoticeCode.AzureRequestFailed, result.ErrorNotice!.Code);
        Assert.Equal(429, result.ErrorNotice.HttpStatus);
        Assert.Equal(diagnosticId, result.ErrorNotice.DiagnosticId);
    }

    [Fact]
    public async Task LoggingOutOfMemoryDoesNotReplaceTheOriginalQueryFailureAndDiagnosticTextIsBounded()
    {
        var exception = new RequestFailedException(400, "Azure diagnostic " + new string('x', 100_000), "BadArgumentError", null);
        var (runner, options, logger) = CreateRunner(new StubQuery((_, _) => Task.FromException<LogsQueryResult>(exception)));
        var result = await runner.TryQueryAsync(options.Workspaces[0], "Event", ["DC01"], 1440, 15, default);
        Assert.True(Assert.Single(logger.Entries).Length < 2400);
        Assert.Null(Assert.Single(logger.Exceptions));
        Assert.True(result.Error!.Length < 500);
        logger.FailWithOutOfMemory = true;
        result = await runner.TryQueryAsync(options.Workspaces[0], "Event", ["DC01"], 1440, 15, default);
        Assert.Contains("Azure HTTP 400 (BadArgumentError)", result.Error);
        Assert.Contains("logging also failed due to insufficient memory", result.Error);
        Assert.True(result.ErrorNotice!.DiagnosticLoggingFailed);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1001, false)]
    [InlineData(1000, true)]
    [InlineData(1001, true)]
    public async Task TableLimitsApplyBeforeFetchingAndWarningsReachBothPublishedAndDraftResults(int rows, bool draft)
    {
        var client = new StubQuery((_, _) => Task.FromResult(Result(
            Enumerable.Range(1, rows).Select(index => $"Event{index:0000}").ToArray())));
        var (runner, _, _) = CreateRunner(client);
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-netlogon-errors");
        query.Workspaces = [new()
        {
            WorkspaceKey = "production", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded
        }];
        ComputerInventoryEntry[] systems = [new() { SubscriptionId = Subscription, Computer = "DC01" }];
        var result = draft
            ? await runner.ExecuteDraftAsync(query, "production", systems, 1440)
            : await runner.ExecuteAsync(query, systems, 1440);
        Assert.StartsWith("set truncationmaxsize=4194304;", client.Kql);
        Assert.EndsWith("| take 1001", client.Kql);
        Assert.Contains(query.Kql, client.Kql);
        var table = Assert.Single(result.Tables!);
        Assert.Equal(1000, table.Rows.Count);
        Assert.Equal("Event0001", table.Rows[0][0]);
        Assert.Equal("Event1000", table.Rows[^1][0]);
        if (rows > 1000)
        {
            Assert.Contains("limited to 1000 rows", Assert.Single(result.Warnings).Message);
            Assert.Contains("More rows exist", result.Warnings[0].Message);
            Assert.Equal(AnalysisQueryNoticeCode.TableRowLimit, result.Warnings[0].Notice!.Code);
        }
        else
            Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TableSafetyWrapperSupportsLetStatementsAndDoesNotLimitChartAggregationInputs()
    {
        const string kql = "let events = Event | where Computer in~ (_SelectedSystems);\nevents | order by TimeGenerated desc; // keep the final comment";
        var table = AnalysisQueryRunner.BuildEffectiveKql(kql, ["DC01"], 15, visualizationType: AnalysisQueryVisualizationTypes.Table);
        Assert.Contains("let _MonitoringTableResult = () {\n" + kql + "\n};", table);
        Assert.EndsWith("| take 1001", table);
        var chart = AnalysisQueryRunner.BuildEffectiveKql(kql, ["DC01"], 15);
        Assert.EndsWith(kql, chart);
        Assert.DoesNotContain("take 1001", chart);
        Assert.DoesNotContain("truncationmaxsize", chart);
    }

    [Fact]
    public async Task PartialErrorsAreExplicitAndPartialRowsAreNeverPresentedAsCompleteResults()
    {
        var client = new StubQuery((_, _) => Task.FromResult(ModelReaderWriter.Read<LogsQueryResult>(
            BinaryData.FromString("""
            {"tables":[{"name":"PrimaryResult","columns":[{"name":"Computer","type":"string"}],"rows":[["DC01"]]}],
             "error":{"code":"PartialError","message":"The query exceeded its resource limits."}}
            """))!));
        var (runner, options, logger) = CreateRunner(client);
        var result = await runner.TryQueryAsync(options.Workspaces[0], "Event", ["DC01"], 1440, 15, default);
        Assert.Contains("PartialError", result.Error);
        Assert.Contains("resource limits", result.Error);
        Assert.Contains("Partial results were not used", result.Error);
        Assert.Empty(result.Rows);
        Assert.True(result.Tables is null || result.Tables.Count == 0);
        Assert.Contains("resource limits", Assert.Single(logger.Entries));
        Assert.Equal(AnalysisQueryNoticeCode.PartialResultsRejected, result.ErrorNotice!.Code);
    }

    [Fact]
    public async Task AdminTestRetainsFailureBesidePreviewAndClearsItWhenTestSelectionChanges()
    {
        var (runner, _, _) = CreateRunner(new StubQuery((_, _) => Task.FromResult(Result([]))));
        var page = new AdminAnalysisQueries();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(AdminAnalysisQueries);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        type.GetProperty("Text", flags)!.SetValue(page, services.GetRequiredService<UiText>());
        type.GetProperty("Logger", flags)!.SetValue(page, services.GetRequiredService<ILogger<AdminAnalysisQueries>>());
        type.GetProperty("QueryRunner", flags)!.SetValue(page, runner);
        type.GetField("editing", flags)!.SetValue(page,
            ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-netlogon-errors"));
        await (Task)type.GetMethod("TestAsync", flags)!.Invoke(page, [])!;
        var localError = Assert.IsType<string>(type.GetField("testError", flags)!.GetValue(page));
        Assert.NotEmpty(localError);
        Assert.Equal(localError, type.GetField("errorMessage", flags)!.GetValue(page));
        Assert.Null(type.GetField("testResult", flags)!.GetValue(page));
        type.GetMethod("ClearTestResult", flags)!.Invoke(page, []);
        Assert.Null(type.GetField("testError", flags)!.GetValue(page));
    }

    [Fact]
    public async Task SdkTimeoutIsAQueryFailureButCallerCancellationStillPropagates()
    {
        var client = new StubQuery((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromException<LogsQueryResult>(new TaskCanceledException("SDK timeout"));
        });
        var (runner, options, logger) = CreateRunner(client);
        var result = await runner.TryQueryAsync(options.Workspaces[0], "Event", ["DC01"], 1440, 15, default);
        Assert.Contains("timed out", result.Error);
        Assert.Single(logger.Entries);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.TryQueryAsync(options.Workspaces[0], "Event", ["DC01"], 1440, 15, cancellation.Token));
        Assert.Single(logger.Entries);
    }

    [Theory]
    [InlineData(AnalysisQueryNoticeCode.UnsupportedVisualization, "Unsupported visualization type.")]
    [InlineData(AnalysisQueryNoticeCode.InconsistentRows, "The query returned inconsistent table rows.")]
    [InlineData(AnalysisQueryNoticeCode.InvalidStatColumns, "Stat queries must return Series and numeric or null Value columns.")]
    [InlineData(AnalysisQueryNoticeCode.MissingColumns, "The query must return Timestamp, Series and Value for lines/status history, or Series and Value for honeycombs/bars/gauges.")]
    [InlineData(AnalysisQueryNoticeCode.InvalidSeriesValues, "The query returned invalid numeric values or series names.")]
    [InlineData(AnalysisQueryNoticeCode.InvalidTimestamp, "The query returned an invalid Timestamp.")]
    public void NormalizerErrorsKeepNeutralContractsAndExplicitNotice(AnalysisQueryNoticeCode code, string message)
    {
        AnalysisQueryColumn[] columns = [new("Series", "string"), new("Value", "real")];
        var type = code switch
        {
            AnalysisQueryNoticeCode.UnsupportedVisualization => "unsupported",
            AnalysisQueryNoticeCode.InconsistentRows => AnalysisQueryVisualizationTypes.Table,
            AnalysisQueryNoticeCode.InvalidStatColumns => AnalysisQueryVisualizationTypes.Stat,
            AnalysisQueryNoticeCode.InvalidSeriesValues => AnalysisQueryVisualizationTypes.Honeycomb,
            _ => AnalysisQueryVisualizationTypes.LineChart
        };
        var table = code switch
        {
            AnalysisQueryNoticeCode.InconsistentRows => new AnalysisQueryTable("original", columns, [["one"]]),
            AnalysisQueryNoticeCode.InvalidStatColumns => new("original", columns, [["series", "not numeric"]]),
            AnalysisQueryNoticeCode.InvalidSeriesValues => new("original", columns, [[null, "1"]]),
            AnalysisQueryNoticeCode.InvalidTimestamp => new("original", [.. columns, new("Timestamp", "datetime")], [["series", "1", "invalid"]]),
            _ => new("original", columns, [])
        };
        var result = AnalysisQueryRunner.NormalizeResult(table, type);
        Assert.Equal(message, result.Error);
        Assert.Equal(code, result.ErrorNotice!.Code);
        Assert.Empty(result.Rows);
        Assert.Null(result.Tables);
        Assert.Null(result.Warning);
        Assert.Null(result.WarningNotice);
        var (rows, error, tables, warning) = result;
        var plain = new AnalysisQueryRunResult(rows, error, tables, warning);
        Assert.Equal(JsonSerializer.Serialize(plain), JsonSerializer.Serialize(result));
        Assert.Null(JsonSerializer.Deserialize<AnalysisQueryRunResult>(JsonSerializer.Serialize(result))!.ErrorNotice);
    }

    [Theory]
    [InlineData("en-US", "azure")]
    [InlineData("de-DE", "azure")]
    [InlineData("en-US", "timeout")]
    [InlineData("de-DE", "timeout")]
    [InlineData("en-US", "authentication")]
    [InlineData("de-DE", "authentication")]
    [InlineData("en-US", "internal")]
    [InlineData("de-DE", "internal")]
    [InlineData("en-US", "partial")]
    [InlineData("de-DE", "partial")]
    [InlineData("en-US", "logging")]
    [InlineData("de-DE", "logging")]
    public async Task RealMixedWorkspaceFailuresAreLocalizedAndKeepSuccessfulRowsAndDiagnosticLogs(string culture, string failure)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var calls = 0;
        var partial = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString("""
            {"tables":[{"name":"PrimaryResult","columns":[{"name":"Computer","type":"string"}],"rows":[["discarded"]]}],
            "error":{"code":"PrivatePartialError","message":"private provider detail"}}
            """))!;
        var client = new StubQuery((_, _) =>
        {
            if (calls++ > 0) return Task.FromResult(Result(["kept"]));
            if (failure == "partial") return Task.FromResult(partial);
            Exception exception = failure switch
            {
                "azure" or "logging" => new RequestFailedException(429, "private provider detail", "PrivateVendorCode", null),
                "timeout" => new OperationCanceledException("private provider detail"),
                "authentication" => new Azure.Identity.AuthenticationFailedException("private provider detail"),
                _ => new InvalidOperationException("private provider detail")
            };
            return Task.FromException<LogsQueryResult>(exception);
        });
        const string otherSubscription = "22222222-2222-2222-2222-222222222222";
        var (runner, options, logger) = CreateRunner(client,
        [
            new() { Key = "production", DisplayName = "Production", SubscriptionIds = [Subscription] },
            new() { Key = "other", DisplayName = "Other <workspace>", SubscriptionIds = [otherSubscription] }
        ]);
        logger.FailWithOutOfMemory = failure == "logging";
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-netlogon-errors");
        query.Workspaces = options.Workspaces.Select(workspace => new AnalysisQueryWorkspace
        {
            WorkspaceKey = workspace.Key, Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded
        }).ToList();
        ComputerInventoryEntry[] systems =
        [
            new() { SubscriptionId = otherSubscription, Computer = "failed-system" },
            new() { SubscriptionId = Subscription, Computer = "kept-system" }
        ];
        var result = await runner.ExecuteAsync(query, systems, 1440);
        Assert.Equal(2, calls);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Other <workspace>", warning.Workspace);
        var code = failure switch
        {
            "azure" or "logging" => AnalysisQueryNoticeCode.AzureRequestFailed,
            "timeout" => AnalysisQueryNoticeCode.RequestTimeout,
            "authentication" => AnalysisQueryNoticeCode.AuthenticationFailed,
            "partial" => AnalysisQueryNoticeCode.PartialResultsRejected,
            _ => AnalysisQueryNoticeCode.QueryFailed
        };
        Assert.Equal(code, warning.Notice!.Code);
        Assert.Equal(failure == "logging", warning.Notice.DiagnosticLoggingFailed);
        Assert.True(Guid.TryParseExact(warning.Notice.DiagnosticId, "N", out _));
        Assert.Contains(warning.Notice.DiagnosticId!, warning.Message);
        Assert.Equal("kept", Assert.Single(Assert.Single(result.Tables!).Rows)[0]);
        Assert.Equal("Production", result.Tables![0].Workspace);
        if (failure != "logging")
        {
            Assert.Contains(warning.Notice.DiagnosticId!, Assert.Single(logger.Entries));
            Assert.Contains("private provider detail", logger.Entries[0]);
        }
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var original = JsonSerializer.Serialize(result);
        var raw = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisResultView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Result"] = result }))).ToHtmlString());
        var html = WebUtility.HtmlDecode(raw);
        var expected = code == AnalysisQueryNoticeCode.AzureRequestFailed
            ? text.Format($"AnalysisWarnings.{code}", 429) : text[$"AnalysisWarnings.{code}"];
        expected = text.Format("AnalysisWarnings.WithDiagnostic", expected, warning.Notice.DiagnosticId!);
        if (failure == "logging") expected = text.Format("AnalysisWarnings.LoggingFailed", expected);
        Assert.Contains(expected, html);
        Assert.Contains(warning.Workspace, html);
        Assert.Contains("kept", html);
        Assert.DoesNotContain("discarded", html);
        Assert.DoesNotContain("private provider detail", html);
        Assert.DoesNotContain("PrivateVendorCode", html);
        Assert.DoesNotContain("PrivatePartialError", html);
        Assert.DoesNotContain("<workspace>", raw);
        Assert.Equal(original, JsonSerializer.Serialize(result));
        Assert.Equal(JsonSerializer.Serialize(new AnalysisQueryWarning(warning.Workspace, warning.Message)),
            JsonSerializer.Serialize(warning));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task RealDraftAndPublishedRowLimitWarningsLocalizeAtExactThreshold(string culture, bool draft)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var client = new StubQuery((_, _) => Task.FromResult(Result(
            Enumerable.Range(1, 1001).Select(index => $"Event{index:0000}").ToArray())));
        var (runner, _, _) = CreateRunner(client);
        var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-netlogon-errors");
        query.Workspaces = [new()
        {
            WorkspaceKey = "production", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded
        }];
        ComputerInventoryEntry[] systems = [new() { SubscriptionId = Subscription, Computer = "DC01" }];
        var result = draft ? await runner.ExecuteDraftAsync(query, "production", systems, 1440)
            : await runner.ExecuteAsync(query, systems, 1440);
        Assert.Equal(1000, Assert.Single(result.Tables!).Rows.Count);
        Assert.Equal("Event1000", result.Tables![0].Rows[^1][0]);
        Assert.EndsWith("| take 1001", client.Kql);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(AnalysisQueryNoticeCode.TableRowLimit, warning.Notice!.Code);
        Assert.Null(warning.Notice.DiagnosticId);
        Assert.Null(warning.Notice.HttpStatus);
        Assert.False(warning.Notice.DiagnosticLoggingFailed);
        var original = JsonSerializer.Serialize(result);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisResultView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Result"] = result }))).ToHtmlString()));
        Assert.Contains(text.Format("AnalysisWarnings.TableRowLimit", 1000), html);
        Assert.Contains("Event0001", html);
        Assert.Contains("Event0050", html);
        Assert.Contains(text.Format("AnalysisResults.Rows", "1", "50", "1000"), html);
        Assert.DoesNotContain("Event0051", html);
        Assert.DoesNotContain("Event1001", html);
        Assert.Equal(original, JsonSerializer.Serialize(result));
        Assert.Equal(JsonSerializer.Serialize(new AnalysisQueryWarning(warning.Workspace, warning.Message)),
            JsonSerializer.Serialize(warning));
    }

    private static (AnalysisQueryRunner Runner, CrossTenantLogAnalyticsOptions Options, QueryLogger Logger) CreateRunner(
        StubQuery client, CrossTenantLogAnalyticsWorkspaceOptions[]? workspaces = null)
    {
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true, ApplicationClientId = "application",
            Workspaces = workspaces ?? [new() { Key = "production", DisplayName = "Production", SubscriptionIds = [Subscription] }]
        };
        var logger = new QueryLogger();
        return (new(options, new SecretProvider(), new TargetWorkspaceResolver(options), client, logger), options, logger);
    }

    private static LogsQueryResult Result(string[] computers)
    {
        return ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromObjectAsJson(new
        {
            tables = new[]
            {
                new
                {
                    name = "PrimaryResult",
                    columns = new[] { new { name = "Computer", type = "string" }, new { name = "RenderedDescription", type = "string" } },
                    rows = computers.Select(computer => new[] { computer, $"Netlogon event on {computer}" }).ToArray()
                }
            }
        }))!;
    }

    private sealed class StubQuery(Func<string, CancellationToken, Task<LogsQueryResult>> execute) : IAnalysisLogAnalyticsQuery
    {
        public string Kql { get; private set; } = "";
        public string ApplicationClientId { get; private set; } = "";
        public int Minutes { get; private set; }

        public Task<LogsQueryResult> QueryAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
            string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Kql = kql;
            ApplicationClientId = applicationClientId;
            Minutes = timeRangeMinutes;
            return execute(kql, cancellationToken);
        }
    }

    private sealed class SecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) => Task.FromResult("not-a-real-secret");
    }

    private sealed class QueryLogger : ILogger<AnalysisQueryRunner>
    {
        public bool FailWithOutOfMemory { get; set; }
        public List<string> Entries { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (FailWithOutOfMemory)
                throw new AggregateException("Error occurred while writing to logger(s).", new OutOfMemoryException());
            Entries.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
