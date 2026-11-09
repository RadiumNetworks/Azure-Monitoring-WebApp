using Azure;
using Azure.Identity;
using Azure.Monitor.Query.Logs.Models;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace MonitoringApp.Tests;

public sealed class PreparedKqlQueryTests
{
    [Theory]
    [InlineData(AnalysisQueryVisualizationTypes.LineChart)]
    [InlineData(AnalysisQueryVisualizationTypes.Table)]
    [InlineData(AnalysisQueryVisualizationTypes.Honeycomb)]
    [InlineData(AnalysisQueryVisualizationTypes.BarChart)]
    [InlineData(AnalysisQueryVisualizationTypes.BarGauge)]
    [InlineData(AnalysisQueryVisualizationTypes.Stat)]
    [InlineData(AnalysisQueryVisualizationTypes.StatusHistory)]
    public void InteractivePreparationCopiesInputsAndKeepsTheCompleteExecutionContext(string visualizationType)
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var systems = new List<string> { "DC02", "DC01" };
        var query = KqlQueryBuilder.PrepareInteractive(
            options.Workspaces[0], "print Value = 1", systems, 30, 240, "NC", visualizationType);
        var originalKql = query.EffectiveKql;

        systems[0] = "changed";
        systems.Clear();

        Assert.Equal(KqlExecutionProfile.Interactive, query.Profile);
        Assert.Same(options.Workspaces[0], query.Workspace);
        Assert.Equal(["DC02", "DC01"], query.Systems);
        Assert.Equal(30, query.BinMinutes);
        Assert.Equal(240, query.ApiTimeRangeMinutes);
        Assert.Equal("NC", query.GroupBy);
        Assert.Equal(visualizationType, query.VisualizationType);
        var preview = query.CreatePreview();
        Assert.Equal(originalKql, preview.EffectiveKql);
        Assert.Same(query.Systems, preview.Systems);
        var collection = Assert.IsAssignableFrom<IList<string>>(preview.Systems);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection[0] = "changed");
        Assert.Equal(["DC01", "DC02"], query.CreatePreview(sortSystems: true).Systems);
        Assert.Equal(["DC02", "DC01"], query.Systems);
    }

    [Fact]
    public void ScheduledPreparationKeepsItsOwnWindowLimitsAndStatProfile()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var start = new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var systems = new[] { "DC01" };

        var query = KqlQueryBuilder.PrepareScheduled(
            options.Workspaces[0], "print Series = 'DC01', Value = 1", systems, start, end, 1503);
        systems[0] = "changed";

        Assert.Equal(KqlExecutionProfile.Scheduled, query.Profile);
        Assert.Equal(AnalysisQueryVisualizationTypes.Stat, query.VisualizationType);
        Assert.Equal(1, query.BinMinutes);
        Assert.Equal(1503, query.ApiTimeRangeMinutes);
        Assert.Equal(["DC01"], query.Systems);
        Assert.Contains("let _WindowStart = datetime(2026-11-02T11:00:00.0000000Z);", query.EffectiveKql);
        Assert.Contains("let _WindowEnd = datetime(2026-11-03T11:00:00.0000000Z);", query.EffectiveKql);
        Assert.Contains("set truncationmaxsize=1048576;", query.EffectiveKql);
        Assert.DoesNotContain("4194304", query.EffectiveKql);
        Assert.DoesNotContain("_MonitoringTableResult", query.EffectiveKql);
        Assert.EndsWith("| take 1001", query.EffectiveKql);
        Assert.Equal(query.EffectiveKql, query.CreatePreview().EffectiveKql);
    }

    [Fact]
    public async Task PublishedPreviewMatchesExecutionOrderAndAllWorkspaceRequestsArePreparedBeforeSecretAccess()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid(
        [
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "second", "22222222-2222-2222-2222-222222222222", "second.example"),
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "first", "11111111-1111-1111-1111-111111111111", "first.example")
        ]);
        var fixture = new Fixture(options);
        var definition = fixture.Definition();
        var source = definition.Kql;
        var systems = new List<ComputerInventoryEntry>
        {
            new() { Computer = "DC02", SubscriptionId = options.Workspaces[0].SubscriptionIds[0] },
            new() { Computer = "DC01", SubscriptionId = options.Workspaces[0].SubscriptionIds[0] },
            new() { Computer = "DC03", SubscriptionId = options.Workspaces[1].SubscriptionIds[0] }
        };
        var previews = fixture.Runner.CreateExecutionPreviews(definition, systems, 60);
        Assert.Equal(["first", "second"], previews.Select(preview => preview.WorkspaceKey));
        Assert.Equal(["DC01", "DC02"], previews[1].Systems);
        Assert.Contains("""dynamic(["DC02","DC01"])""", previews[1].EffectiveKql);
        fixture.Secret.OnRead = () =>
        {
            fixture.Secret.OnRead = null;
            systems[0].Computer = "changed";
            systems.Clear();
            definition.Kql = "print Value = 999";
            definition.VisualizationType = AnalysisQueryVisualizationTypes.Stat;
            definition.DefaultBinMinutes = 60;
        };

        var result = await fixture.Runner.ExecuteAsync(definition, systems, 60);

        Assert.Empty(result.Warnings);
        Assert.Equal(AnalysisQueryVisualizationTypes.Table, result.VisualizationType);
        Assert.Equal(15, result.BinMinutes);
        Assert.Equal(["second", "first"], fixture.Client.Requests.Select(request => request.Workspace.Key));
        foreach (var request in fixture.Client.Requests)
        {
            Assert.Equal(previews.Single(preview => preview.WorkspaceKey == request.Workspace.Key).EffectiveKql, request.Kql);
            Assert.Contains(source, request.Kql);
            Assert.DoesNotContain("changed", request.Kql);
            Assert.DoesNotContain("999", request.Kql);
            Assert.Equal(60, request.Minutes);
        }
    }

    [Fact]
    public async Task DraftExecutionUsesItsPreparedQueryAndResultProfileAfterInputsChange()
    {
        var fixture = new Fixture();
        var definition = fixture.Definition();
        var systems = new List<ComputerInventoryEntry>
        {
            new() { Computer = "DC02", SubscriptionId = fixture.Options.Workspaces[0].SubscriptionIds[0] },
            new() { Computer = "DC01", SubscriptionId = fixture.Options.Workspaces[0].SubscriptionIds[0] }
        };
        var preview = fixture.Runner.CreateDraftExecutionPreview(definition, "primary", systems, 60);
        fixture.Secret.OnRead = () =>
        {
            systems.Clear();
            definition.Kql = "print Value = 999";
            definition.VisualizationType = AnalysisQueryVisualizationTypes.Stat;
            definition.DefaultBinMinutes = 60;
        };

        var result = await fixture.Runner.ExecuteDraftAsync(definition, "primary", systems, 60);

        Assert.Equal(preview.EffectiveKql, Assert.Single(fixture.Client.Requests).Kql);
        Assert.Equal(["DC01", "DC02"], preview.Systems);
        Assert.Empty(result.Warnings);
        Assert.Equal(AnalysisQueryVisualizationTypes.Table, result.VisualizationType);
        Assert.Equal(15, result.BinMinutes);
    }

    [Theory]
    [InlineData("http", "Azure HTTP 429")]
    [InlineData("authentication", "authentication failed")]
    [InlineData("timeout", "timed out")]
    [InlineData("partial", "Partial results were not used")]
    public async Task PreparedExecutionRetainsVisibleFailureCategoriesAndRejectsPartialData(string failure, string message)
    {
        var fixture = new Fixture();
        fixture.Client.Execute = (_, _) => failure switch
        {
            "http" => Task.FromException<LogsQueryResult>(new RequestFailedException(429, "Throttled")),
            "authentication" => Task.FromException<LogsQueryResult>(new AuthenticationFailedException("Not authenticated")),
            "timeout" => Task.FromException<LogsQueryResult>(new TaskCanceledException()),
            "partial" => Task.FromResult(ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString("""
                {"tables":[{"name":"PrimaryResult","columns":[{"name":"Computer","type":"string"}],"rows":[["DC01"]]}],
                 "error":{"code":"PartialError","message":"Incomplete."}}
                """))!),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        var query = KqlQueryBuilder.PrepareInteractive(
            fixture.Options.Workspaces[0], "print Computer = 'DC01'", ["DC01"], 15, 60,
            visualizationType: AnalysisQueryVisualizationTypes.Table);

        var result = await fixture.Runner.TryQueryAsync(query, default);

        Assert.Contains(message, result.Error);
        Assert.Contains("Diagnostic ID:", result.Error);
        Assert.Empty(result.Rows);
        Assert.True(result.Tables is null || result.Tables.Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnglishAndGermanExecutionSendIdenticalKqlAndKeepOriginalResultData(bool draft)
    {
        var fixture = new Fixture();
        var definition = fixture.Definition();
        var original = JsonSerializer.Serialize(definition);
        var systems = new[]
        {
            new ComputerInventoryEntry { Computer = "DC02", SubscriptionId = fixture.Options.Workspaces[0].SubscriptionIds[0] },
            new ComputerInventoryEntry { Computer = "DC01", SubscriptionId = fixture.Options.Workspaces[0].SubscriptionIds[0] }
        };
        fixture.Client.Execute = (_, _) => Task.FromResult(ModelReaderWriter.Read<LogsQueryResult>(
            BinaryData.FromString("""
                {"tables":[{"name":"OriginalResult","columns":[{"name":"Computer","type":"string"},{"name":"RawStatus","type":"string"}],
                 "rows":[["DC<01> &","Failed"],["DC\"02","Warnung"]]}]}
                """))!);

        async Task<AnalysisQueryResponse> Run(string culture)
        {
            using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
            return draft
                ? await fixture.Runner.ExecuteDraftAsync(definition, "primary", systems, 60)
                : await fixture.Runner.ExecuteAsync(definition, systems, 60);
        }

        var english = await Run("en-US");
        var german = await Run("de-DE");
        Assert.Equal(2, fixture.Client.Requests.Count);
        var first = fixture.Client.Requests[0];
        var second = fixture.Client.Requests[1];
        Assert.Equal(first.Kql, second.Kql);
        Assert.Contains(definition.Kql, first.Kql);
        Assert.Equal(60, first.Minutes);
        Assert.Equal(first.Minutes, second.Minutes);
        Assert.Same(first.Workspace, second.Workspace);
        Assert.Equal(first.Token, second.Token);
        Assert.Empty(english.Warnings);
        Assert.Empty(german.Warnings);
        Assert.Equal(english.QueryDefinitionId, german.QueryDefinitionId);
        Assert.Equal(english.Name, german.Name);
        Assert.Equal(english.Version, german.Version);
        Assert.Equal(english.Unit, german.Unit);
        Assert.Equal(english.BinMinutes, german.BinMinutes);
        Assert.Equal(english.Scope, german.Scope);
        Assert.Equal(english.VisualizationType, german.VisualizationType);
        Assert.Equal(JsonSerializer.Serialize(english.Visualization), JsonSerializer.Serialize(german.Visualization));
        Assert.Equal(JsonSerializer.Serialize(english.Series), JsonSerializer.Serialize(german.Series));
        Assert.Equal(JsonSerializer.Serialize(english.Tables), JsonSerializer.Serialize(german.Tables));
        var table = Assert.Single(german.Tables!);
        Assert.Equal(new[] { "Computer", "RawStatus" }, table.Columns.Select(column => column.Name));
        Assert.Equal(new[] { "DC<01> &", "Failed" }, table.Rows[0]);
        Assert.Equal(new[] { "DC\"02", "Warnung" }, table.Rows[1]);
        Assert.Equal(original, JsonSerializer.Serialize(definition));
    }

    [Fact]
    public async Task PreparedExecutionPropagatesCallerCancellation()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Client.Execute = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<LogsQueryResult>(token);
        };
        var query = KqlQueryBuilder.PrepareInteractive(
            fixture.Options.Workspaces[0], "print Value = 1", [], 15, 60);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Runner.TryQueryAsync(query, cancellation.Token));

        Assert.Equal(cancellation.Token, Assert.Single(fixture.Client.Requests).Token);
    }

    [Fact]
    public async Task CompatibilityExecutionKeepsPreparationFailuresInsideTheDiagnosticBoundary()
    {
        var fixture = new Fixture();

        var result = await fixture.Runner.TryQueryAsync(
            fixture.Options.Workspaces[0], "print Value = 1", [], 60, 15, default, groupBy: "unsupported");

        Assert.Contains("supported replication grouping", result.Error);
        Assert.Contains("Diagnostic ID:", result.Error);
        Assert.Equal(1, fixture.Secret.Calls);
        Assert.Empty(fixture.Client.Requests);
    }

    [Theory]
    [InlineData(AnalysisQueryScopes.Workspace)]
    [InlineData(AnalysisQueryScopes.SelectedSystems)]
    public void ScheduledAndInteractiveValidationKeepTheirDifferentTimeMacros(string scope)
    {
        var source = "Event | where TimeGenerated >= _WindowStart and TimeGenerated < _WindowEnd";
        if (scope == AnalysisQueryScopes.SelectedSystems)
            source += " | where Computer in~ (_SelectedSystems)";
        source += " | summarize Value = count() | extend Series = 'Workspace'";
        Assert.Empty(AnalysisQueryValidator.ValidateScheduledKql(source, scope));
        var relative = source.Replace("_WindowStart", "ago(_TimePeriod)", StringComparison.Ordinal)
            .Replace("_WindowEnd", "now()", StringComparison.Ordinal);

        Assert.Empty(AnalysisQueryValidator.ValidateKql(relative, AnalysisQueryVisualizationTypes.Stat, scope));
        Assert.Contains("KQL must use _WindowStart and _WindowEnd to restrict the evaluation window.",
            AnalysisQueryValidator.ValidateScheduledKql(relative, scope));
    }

    private sealed class Fixture
    {
        public CrossTenantLogAnalyticsOptions Options { get; }
        public SecretProvider Secret { get; } = new();
        public QueryClient Client { get; } = new();
        public AnalysisQueryRunner Runner { get; }
        public Fixture(CrossTenantLogAnalyticsOptions? options = null)
        {
            Options = options ?? CrossTenantLogAnalyticsOptionsTests.CreateValid();
            Runner = new(Options, Secret, new TargetWorkspaceResolver(Options), Client);
        }
        public AnalysisQueryDefinition Definition()
        {
            var definition = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-netlogon-errors");
            definition.DefaultBinMinutes = 15;
            definition.Workspaces = Options.Workspaces.Select(workspace => new AnalysisQueryWorkspace
            {
                WorkspaceKey = workspace.Key, Enabled = true,
                LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded
            }).ToArray();
            return definition;
        }
    }

    private sealed class SecretProvider : ILogAnalyticsSecretProvider
    {
        public Action? OnRead { get; set; }
        public int Calls { get; private set; }
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            OnRead?.Invoke();
            return Task.FromResult("test-secret");
        }
    }

    private sealed class QueryClient : IAnalysisLogAnalyticsQuery
    {
        public List<(CrossTenantLogAnalyticsWorkspaceOptions Workspace, string Kql, int Minutes, CancellationToken Token)> Requests { get; } = [];
        public Func<string, CancellationToken, Task<LogsQueryResult>> Execute { get; set; } =
            (_, _) => Task.FromResult(ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
                """{"tables":[{"name":"PrimaryResult","columns":[{"name":"Computer","type":"string"}],"rows":[["DC01"]]}]}"""))!);
        public Task<LogsQueryResult> QueryAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
            string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Assert.Equal("test-secret", secret);
            Requests.Add((workspace, kql, timeRangeMinutes, cancellationToken));
            return Execute(kql, cancellationToken);
        }
    }
}
