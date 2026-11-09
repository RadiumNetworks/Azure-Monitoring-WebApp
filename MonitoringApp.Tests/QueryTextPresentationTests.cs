using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class QueryTextPresentationTests
{
    [Theory]
    [InlineData("name", false)]
    [InlineData("name", true)]
    [InlineData("description", false)]
    [InlineData("description", true)]
    public async Task MissingGermanCatalogFieldFallsBackIndividuallyToItsExactEnglishOriginal(string field, bool missing)
    {
        using var selection = new Cultures("de-DE");
        await using var services = Services();
        var key = field == "name" ? Processor.NameResourceKey : Processor.DescriptionResourceKey!;
        var real = services.GetRequiredService<IStringLocalizer<UiResources>>();
        var text = new UiText(new MissingCatalogField(real, key, missing),
            services.GetRequiredService<ILogger<UiText>>());
        var display = new QueryTextPresentation(text, services.GetRequiredService<ILogger<QueryTextPresentation>>());
        var query = Definition();
        Assert.Equal(field == "name" ? Processor.Name : real[Processor.NameResourceKey].Value, display.Name(query));
        Assert.Equal(field == "description" ? Processor.Description : real[Processor.DescriptionResourceKey!].Value,
            display.Description(query));
        Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        Assert.Throws<InvalidOperationException>(() => UiResources.GetEnglish(
            Processor.NameResourceKey.Replace(".V1.", ".V999.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelUserScopesRenderSharedImmutableResultWithoutCultureOrDataLeaks(bool trusted)
    {
        var parentUi = CultureInfo.CurrentUICulture;
        var parentCulture = CultureInfo.CurrentCulture;
        await using var services = Services();
        var query = Definition();
        var response = Response(query, AnalysisQueryVisualizationTypes.Table, trusted);
        var original = JsonSerializer.Serialize(response);
        var tasks = new[] { "en-US", "de-DE" }.Select(culture => Task.Run(async () =>
        {
            using var selection = new Cultures(culture);
            await using var scope = services.CreateAsyncScope();
            var text = scope.ServiceProvider.GetRequiredService<UiText>();
            var display = scope.ServiceProvider.GetRequiredService<QueryTextPresentation>();
            var expected = trusted ? text[Processor.NameResourceKey] : Processor.Name;
            for (var iteration = 0; iteration < 8; iteration++)
            {
                await Task.Yield();
                Assert.Equal(expected, display.Name(response));
                Assert.Contains(expected, WebUtility.HtmlDecode(await Render<AnalysisResultView>(scope.ServiceProvider,
                    new() { ["Result"] = response })));
                Assert.Equal(culture, CultureInfo.CurrentUICulture.Name);
                Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
                Assert.Equal(original, JsonSerializer.Serialize(response));
            }
            return expected;
        })).ToArray();
        var names = await Task.WhenAll(tasks);
        Assert.Equal(Processor.Name, names[0]);
        Assert.Equal(trusted ? "Prozessorauslastung" : Processor.Name, names[1]);
        Assert.Equal(parentUi, CultureInfo.CurrentUICulture);
        Assert.Equal(parentCulture, CultureInfo.CurrentCulture);
        Assert.Equal(original, JsonSerializer.Serialize(response));
    }

    public static IEnumerable<object[]> CatalogCases() =>
        from culture in new[] { "en-US", "de-DE" }
        from entry in QueryTemplateCatalog.Entries
        select new object[] { culture, entry.TemplateId };

    [Theory]
    [MemberData(nameof(CatalogCases))]
    public async Task EveryCatalogFieldProjectsOnlyWithTrustedUnmodifiedEvidence(string culture, string id)
    {
        using var selection = new Cultures(culture);
        await using var services = Services();
        var text = services.GetRequiredService<UiText>();
        var display = services.GetRequiredService<QueryTextPresentation>();
        var entry = QueryTemplateCatalog.GetActive(id);
        Assert.Equal(text[entry.NameResourceKey], display.TemplateName(id));
        if (entry.Library == "Scheduled")
        {
            var rule = new ScheduledQueryRule { Name = entry.Name };
            var original = JsonSerializer.Serialize(rule);
            Assert.Equal(entry.Name, display.Name(rule));
            rule.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(entry);
            Assert.Equal(text[entry.NameResourceKey], display.Name(rule));
            Assert.Equal(original, JsonSerializer.Serialize(rule));
            rule.Name = "Own <name> &";
            Assert.Equal(rule.Name, display.Name(rule));
            rule.StandardTextProvenanceJson = StandardQueryTextProvenance.Retain(
                rule.StandardTextProvenanceJson, true, entry.Name, null, rule.Name, null);
            rule.Name = entry.Name;
            Assert.Equal(entry.Name, display.Name(rule));
        }
        else
        {
            var query = new AnalysisQueryDefinition { Name = entry.Name, Description = entry.Description! };
            var original = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query));
            Assert.Equal(entry.Name, display.Name(query));
            Assert.Equal(entry.Description, display.Description(query));
            query.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(entry);
            Assert.Equal(text[entry.NameResourceKey], display.Name(query));
            Assert.Equal(text[entry.DescriptionResourceKey!], display.Description(query));
            query.Name = "Own <name> &";
            Assert.Equal(query.Name, display.Name(query));
            Assert.Equal(text[entry.DescriptionResourceKey!], display.Description(query));
            query.StandardTextProvenanceJson = StandardQueryTextProvenance.Retain(
                query.StandardTextProvenanceJson, false, entry.Name, entry.Description, query.Name, query.Description);
            query.Name = entry.Name;
            Assert.Equal(entry.Name, display.Name(query));
            Assert.Equal(text[entry.DescriptionResourceKey!], display.Description(query));
            query.Description = "Own <description> &";
            Assert.Equal(query.Description, display.Description(query));
            query.Description = entry.Description!;
            Assert.Equal(original, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query)));
        }
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    public static IEnumerable<object[]> ResultCases() =>
        from culture in new[] { "en-US", "de-DE" }
        from type in AnalysisQueryVisualizationTypes.All
        from trusted in new[] { false, true }
        select new object[] { culture, type, trusted };

    [Theory]
    [MemberData(nameof(ResultCases))]
    public async Task ActualResultCaptionsAndAriaProjectWithoutChangingJsonOrRawValues(string culture, string type, bool trusted)
    {
        using var selection = new Cultures(culture);
        await using var services = Services();
        var query = Definition();
        var response = Response(query, type, trusted);
        var original = JsonSerializer.Serialize(response);
        var expected = trusted ? services.GetRequiredService<UiText>()[Processor.NameResourceKey] : query.Name;
        var html = WebUtility.HtmlDecode(await Render<AnalysisResultView>(services,
            new() { ["Result"] = response }));
        Assert.Contains(expected, html);
        Assert.Contains("Original <workspace>", html);
        Assert.Contains("Original <series>", html);
        Assert.Equal(original, JsonSerializer.Serialize(response));
        Assert.Equal(original, JsonSerializer.Serialize(response with { StandardTextDisplayContext = null }));
        var reimport = JsonSerializer.Deserialize<AnalysisQueryResponse>(original)!;
        Assert.Null(reimport.StandardTextDisplayContext);
        Assert.Equal(query.Name, services.GetRequiredService<QueryTextPresentation>().Name(reimport));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        if (type != AnalysisQueryVisualizationTypes.Table)
        {
            var table = WebUtility.HtmlDecode(await Render<AnalysisResultView>(services,
                new() { ["Result"] = response, ["AllowTableView"] = true, ["ShowTable"] = true }));
            Assert.Contains(expected, table);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedWidgetTitlesStayIndependentOfResultCaptionProvenance(string culture)
    {
        using var selection = new Cultures(culture);
        await using var services = Services();
        var query = Definition();
        var dashboard = new AnalysisDashboardState();
        var widget = dashboard.AddWidget(query.Id, query.Name, 60, 1, 1);
        var original = JsonSerializer.Serialize(dashboard.Document);
        var response = Response(query, AnalysisQueryVisualizationTypes.Table, true);
        var html = WebUtility.HtmlDecode(await Render<PublishedAnalysisDashboardGrid>(services, new()
        {
            ["Document"] = dashboard.Document,
            ["Results"] = new Dictionary<string, AnalysisQueryResponse> { [widget.Id] = response }
        }));
        Assert.Contains($"<strong>{query.Name}</strong>", html);
        Assert.Contains(services.GetRequiredService<UiText>()[Processor.NameResourceKey], html);
        Assert.Equal(original, JsonSerializer.Serialize(dashboard.Document));
    }

    public static IEnumerable<object[]> FailureCases() =>
        from culture in new[] { "en-US", "de-DE" }
        from fault in new[] { "json", "schema", "revision", "field", "kind", "result-name", "result-id", "result-version", "template" }
        select new object[] { culture, fault };

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task InvalidEvidenceIsLoggedAndDisplayedAsOneSafeLocalizedDiagnostic(string culture, string fault)
    {
        using var selection = new Cultures(culture);
        var log = new DisplayLog();
        await using var services = Services(log);
        var display = services.GetRequiredService<QueryTextPresentation>();
        var query = Definition();
        var response = Response(query, AnalysisQueryVisualizationTypes.Table, true);
        switch (fault)
        {
            case "json": query.StandardTextProvenanceJson = "private <invalid JSON>"; break;
            case "schema": query.StandardTextProvenanceJson = query.StandardTextProvenanceJson!.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"); break;
            case "revision": query.StandardTextProvenanceJson = query.StandardTextProvenanceJson!.Replace(Processor.Revision, new string('0', 64)); break;
            case "field": query.StandardTextProvenanceJson = query.StandardTextProvenanceJson!.Replace("\"description\"", "\"kql\""); break;
            case "kind": query.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(QueryTemplateCatalog.GetActive("kql/scheduled/cpu-high")); break;
            case "result-name": response = response with { Name = "Own result" }; break;
            case "result-id": response = response with { QueryDefinitionId = Guid.NewGuid() }; break;
            case "result-version": response = response with { Version = response.Version + 1 }; break;
        }
        var value = fault == "template" ? display.TemplateName("not-a-template")
            : fault.StartsWith("result-", StringComparison.Ordinal) ? display.Name(response) : display.Name(query);
        Assert.Equal(services.GetRequiredService<UiText>()["QueryTemplates.Display.Invalid"], value);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        if (!fault.StartsWith("result-", StringComparison.Ordinal) && fault != "template")
        {
            response = response with { StandardTextDisplayContext = QueryTextDisplayContext.Capture(query) };
            var html = WebUtility.HtmlDecode(await Render<AnalysisResultView>(services, new() { ["Result"] = response }));
            Assert.Contains(value, html);
            Assert.DoesNotContain("private <invalid JSON>", html);
            Assert.DoesNotContain(services.GetRequiredService<UiText>()[Processor.NameResourceKey], html);
        }
    }

    [Theory]
    [InlineData("en-US", false, false)]
    [InlineData("de-DE", false, false)]
    [InlineData("en-US", true, false)]
    [InlineData("de-DE", true, false)]
    [InlineData("en-US", false, true)]
    [InlineData("de-DE", false, true)]
    [InlineData("en-US", true, true)]
    [InlineData("de-DE", true, true)]
    public async Task ActualAsyncExecutionFreezesSourceBeforeAwaitAndDoesNotBorrowNewerPublication(
        string culture, bool draft, bool unproven)
    {
        using var selection = new Cultures(culture);
        await using var services = Services();
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var client = new PausedClient();
        var runner = new AnalysisQueryRunner(options, new Secret(), new TargetWorkspaceResolver(options), client);
        var query = Definition();
        if (unproven) query.StandardTextProvenanceJson = null;
        query.Workspaces = [new()
        {
            WorkspaceKey = options.Workspaces[0].Key, Enabled = true,
            LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded
        }];
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(), Version = 2,
            SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query)),
            StandardTextProvenanceJson = query.StandardTextProvenanceJson
        };
        query.Versions.Add(version);
        query.PublishedVersionId = version.Id;
        var executed = draft ? query : AnalysisQueryRegistry.CreatePublishedDefinition(query);
        var source = QueryTextDisplayContext.Capture(executed);
        var sourceUnit = executed.Unit;
        ComputerInventoryEntry[] systems = [new()
        {
            Computer = "DC01", SubscriptionId = options.Workspaces[0].SubscriptionIds[0]
        }];
        var request = new AnalysisQueryRequest(query.Id, [new(systems[0].SubscriptionId, systems[0].Computer)], 60);
        var batch = new AnalysisDashboardQueryBatch((_, token) => runner.ExecuteAsync(executed, systems, 60, token), default);
        var pending = draft ? runner.ExecuteDraftAsync(executed, options.Workspaces[0].Key, systems, 60)
            : batch.ExecuteAsync(executed, request);
        if (!draft) Assert.Same(pending, batch.ExecuteAsync(executed, request));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(executed.Kql, client.Kql);
        query.Name = executed.Name = "Later <draft> &";
        query.Description = executed.Description = "Later description";
        query.Version = executed.Version = 99;
        query.Kql = executed.Kql = "Later KQL";
        query.Unit = executed.Unit = "Later unit";
        query.VisualizationJson = executed.VisualizationJson = """{"nullText":"Later options"}""";
        query.StandardTextProvenanceJson = executed.StandardTextProvenanceJson = null;
        version.SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query));
        version.StandardTextProvenanceJson = null;
        client.Release.SetResult();
        var response = await pending;
        Assert.Equal(source.Name, response.Name);
        Assert.Equal(source.Version, response.Version);
        Assert.Equal(source, response.StandardTextDisplayContext);
        Assert.Equal(sourceUnit, response.Unit);
        Assert.DoesNotContain("Later", JsonSerializer.Serialize(response));
        var expected = unproven ? source.Name : services.GetRequiredService<UiText>()[Processor.NameResourceKey];
        Assert.Equal(expected, services.GetRequiredService<QueryTextPresentation>().Name(response));
        Assert.Contains(expected, WebUtility.HtmlDecode(await Render<AnalysisResultView>(services, new() { ["Result"] = response })));
        Assert.Equal(1, client.Calls);
        if (!draft) Assert.Equal(1, batch.Count);
        using (new Cultures(culture == "de-DE" ? "en-US" : "de-DE"))
        {
            var opposite = unproven ? source.Name : services.GetRequiredService<UiText>()[Processor.NameResourceKey];
            Assert.Equal(opposite, services.GetRequiredService<QueryTextPresentation>().Name(response));
        }
    }

    private static QueryTemplateCatalogEntry Processor => QueryTemplateCatalog.GetActive("kql/ad/ad-processor");
    private static AnalysisQueryDefinition Definition()
    {
        var query = AnalysisQueryTemplates.Parse(QueryTemplateCatalog.ReadPackagedTemplate(Processor));
        query.StandardTextProvenanceJson = StandardQueryTextProvenance.Create(Processor);
        query.Version = 2;
        return query;
    }

    private static AnalysisQueryResponse Response(AnalysisQueryDefinition query, string type, bool trusted)
    {
        var start = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        return new(query.Id, query.Name, query.Version, query.Unit, start, start.AddHours(1), start.AddHours(1), 10,
            [new("Original <series>", "Original <dimension>", "Original <workspace>", [new(start, 12.5, "Original <label>")])],
            [], type, [new("Original <workspace>", [new("Series", "string"), new("Value", "real")],
                [["Original <series>", "12.5"]])], BinMinutes: 5)
        {
            StandardTextDisplayContext = trusted ? QueryTextDisplayContext.Capture(query) : null
        };
    }

    private static ServiceProvider Services(DisplayLog? log = null)
    {
        var services = new ServiceCollection().AddLogging().AddUiLocalization();
        if (log is not null) services.AddSingleton<ILogger<QueryTextPresentation>>(log);
        return services.BuildServiceProvider();
    }

    private static async Task<string> Render<T>(IServiceProvider services, Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class Cultures : IDisposable
    {
        private readonly CultureInfo previousUi = CultureInfo.CurrentUICulture;
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;
        internal Cultures(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class DisplayLog : ILogger<QueryTextPresentation>
    {
        internal List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    private sealed class MissingCatalogField(IStringLocalizer<UiResources> real, string key, bool missing)
        : IStringLocalizer<UiResources>
    {
        public LocalizedString this[string name] => name == key
            ? new(name, missing ? name : " ", missing) : real[name];
        public LocalizedString this[string name, params object[] arguments] => real[name, arguments];
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => real.GetAllStrings(includeParentCultures);
    }

    private sealed class Secret : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) => Task.FromResult("local-test");
    }

    private sealed class PausedClient : IAnalysisLogAnalyticsQuery
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Kql { get; private set; } = "";
        internal int Calls { get; private set; }
        public async Task<LogsQueryResult> QueryAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
            string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Kql = kql;
            Calls++;
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
                """{"tables":[{"name":"PrimaryResult","columns":[{"name":"Timestamp","type":"datetime"},{"name":"Series","type":"string"},{"name":"Value","type":"real"}],"rows":[["2026-11-08T12:00:00Z","Original series",12.5]]}]}"""))!;
        }
    }
}
