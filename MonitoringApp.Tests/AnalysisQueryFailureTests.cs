using Azure;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.Extensions.Logging;
using System.ClientModel.Primitives;
using System.Reflection;
using MonitoringApp.Components.Pages;

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
    }

    [Fact]
    public async Task AdminTestRetainsFailureBesidePreviewAndClearsItWhenTestSelectionChanges()
    {
        var (runner, _, _) = CreateRunner(new StubQuery((_, _) => Task.FromResult(Result([]))));
        var page = new AdminAnalysisQueries();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(AdminAnalysisQueries);
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

    private static (AnalysisQueryRunner Runner, CrossTenantLogAnalyticsOptions Options, QueryLogger Logger) CreateRunner(StubQuery client)
    {
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true, ApplicationClientId = "application",
            Workspaces = [new() { Key = "production", DisplayName = "Production", SubscriptionIds = [Subscription] }]
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
