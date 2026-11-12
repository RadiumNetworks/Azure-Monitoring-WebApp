using System.ClientModel.Primitives;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class FreeKqlQueryTests
{
    private static CrossTenantLogAnalyticsOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled, ApplicationClientId = "app",
        Workspaces = [new() { Key = "workspace", DisplayName = "Original workspace" }]
    };
    private static LogsQueryResult Response(object[][] rows) => ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
        JsonSerializer.Serialize(new { tables = new[] { new { name = "PrimaryResult",
            columns = new[] { new { name = "Status__Status", type = "string" } }, rows } } })))!;

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ServiceSendsExactKqlAndReturnsAllColumnsWithoutInventoryRequirements(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var client = new Client { Result = Response([["Original <cell> &"]]) };
        var secret = new Secret();
        var service = new FreeKqlQueryService(Options(), secret, client);
        const string kql = " \nprint Status__Status='Original <cell> &' ";
        var result = await service.ExecuteAsync("WORKSPACE", kql, default);
        Assert.Equal(kql, client.Kql);
        Assert.Equal("Status__Status", Assert.Single(Assert.Single(result.Tables).Columns).Name);
        Assert.Equal("Original <cell> &", Assert.Single(Assert.Single(result.Tables).Rows)[0]);
        Assert.False(result.Limited);
        Assert.Equal(1, secret.Calls);
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public async Task ExactRowLimitIsReportedWithoutChangingTheQuery(int count, bool limited)
    {
        var client = new Client { Result = Response(Enumerable.Range(0, count).Select(i => new object[] { i.ToString() }).ToArray()) };
        var service = new FreeKqlQueryService(Options(), new Secret(), client);
        var result = await service.ExecuteAsync("workspace", "print Value=1", default);
        Assert.Equal(limited, result.Limited);
        Assert.Equal(Math.Min(count, 1000), Assert.Single(result.Tables).Rows.Count);
        Assert.Equal("print Value=1", client.Kql);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ExactUtf8TextBudgetIncludesColumns(int extra, bool limited)
    {
        const int columnBytes = 14 + 6;
        var client = new Client { Result = Response([[new string('x', 4 * 1024 * 1024 - columnBytes + extra)]]) };
        var result = await new FreeKqlQueryService(Options(), new Secret(), client).ExecuteAsync("workspace", "print Value=1", default);
        Assert.Equal(limited, result.Limited);
        Assert.Equal(limited ? 0 : 1, Assert.Single(result.Tables).Rows.Count);
    }

    [Fact]
    public async Task SchemaDiscoveryReturnsColumnNamesAndKqlTypesRatherThanDotNetTypes()
    {
        var client = new Client { Result = SchemaResponse([
            ["Counter", "int"], ["Computer", "string"], ["Details", "dynamic"], ["TimeGenerated", "datetime"]]) };
        var secret = new Secret();
        var columns = await new FreeKqlQueryService(Options(), secret, client)
            .GetTableSchemaAsync("WORKSPACE", "Perf", default);
        Assert.Equal("table(@'Perf') | getschema", client.Kql);
        Assert.Equal(["workspace"], client.WorkspaceKeys);
        Assert.Equal([new KqlSchemaColumn("Counter", "int"), new("Computer", "string"),
            new("Details", "dynamic"), new("TimeGenerated", "datetime")], columns);
        Assert.Equal(1, secret.Calls);
    }

    [Theory]
    [InlineData("Odd'Name\\Path")]
    [InlineData("Perf'); print x=1;//")]
    public async Task SchemaDiscoveryTreatsTheTableNameAsAnEscapedLiteral(string name)
    {
        var client = new Client { Result = SchemaResponse([]) };
        Assert.Empty(await new FreeKqlQueryService(Options(), new Secret(), client)
            .GetTableSchemaAsync("workspace", name, default));
        Assert.Equal($"table(@'{name.Replace("'", "''", StringComparison.Ordinal)}') | getschema", client.Kql);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("workspace")]
    [InlineData("empty")]
    [InlineData("cancelled")]
    public async Task InvalidSchemaRequestsNeverCallAzure(string fault)
    {
        var client = new Client();
        var secret = new Secret();
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancelled") cancellation.Cancel();
        Assert.NotNull(await Record.ExceptionAsync(() =>
            new FreeKqlQueryService(Options(fault != "disabled"), secret, client)
                .GetTableSchemaAsync(fault == "workspace" ? "missing" : "workspace",
                    fault == "empty" ? " " : "Perf", cancellation.Token)));
        Assert.Equal(0, secret.Calls);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("""{"tables":[]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"ColumnName","type":"string"}],"rows":[]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"ColumnName","type":"string"},{"name":"ColumnType","type":"string"}],"rows":[["Value",""]]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"ColumnName","type":"string"},{"name":"ColumnType","type":"string"}],"rows":[[null,"int"]]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"ColumnName","type":"string"},{"name":"ColumnType","type":"long"}],"rows":[["Value",42]]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"ColumnName","type":"string"},{"name":"ColumnType","type":"string"}],"rows":[["Value","int"]]}],"error":{"code":"PartialError","message":"Incomplete"}}""")]
    public async Task SchemaDiscoveryRejectsMalformedAndPartialResponses(string json)
    {
        var client = new Client { Result = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(json))! };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FreeKqlQueryService(Options(), new Secret(), client).GetTableSchemaAsync("workspace", "Perf", default));
    }

    [Fact]
    public async Task SchemaDiscoveryDoesNotTruncateColumnsAndHonorsCancellation()
    {
        var result = SchemaResponse(Enumerable.Range(0, 1005).Select(index => new[] { $"Column_{index}", "dynamic" }).ToArray());
        var client = new Client { Result = result };
        var service = new FreeKqlQueryService(Options(), new Secret(), client);
        Assert.Equal(1005, (await service.GetTableSchemaAsync("workspace", "Perf", default)).Count);
        using var cancellation = new CancellationTokenSource();
        client.BeforeResult = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetTableSchemaAsync("workspace", "Perf", cancellation.Token));
    }

    private static LogsQueryResult SchemaResponse(string[][] columns) =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            tables = new[] { new { name = "PrimaryResult",
                columns = new[] { new { name = "ColumnType", type = "string" }, new { name = "ColumnName", type = "string" },
                    new { name = "DataType", type = "string" } },
                rows = columns.Select(column => new[] { column[1], column[0], "System.Object" }).ToArray() } }
        })))!;

    [Fact]
    public async Task DiscoverySendsExactQueryForEachDistinctWorkspaceAndUnionsCaseSensitiveNames()
    {
        var client = new Client
        {
            QueryHandler = (workspace, _) => Task.FromResult(CatalogResponse(workspace.Key == "first"
                ? [["Perf"], ["Shared"], ["lower"]] : [["Event"], ["Shared"], ["Lower"]]))
        };
        var secret = new Secret();
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true, ApplicationClientId = "app",
            Workspaces = [new() { Key = "first" }, new() { Key = "second" }]
        };
        var names = await new FreeKqlQueryService(options, secret, client)
            .GetTableNamesAsync(["FIRST", "second", "first"], default);
        Assert.Equal(["Event", "Lower", "Perf", "Shared", "lower"], names);
        Assert.Equal(["first", "second"], client.WorkspaceKeys);
        Assert.All(client.Queries, query =>
            Assert.Equal("search *\n| summarize count() by $table\n| sort by $table", query));
        Assert.Equal(1, secret.Calls);
    }

    [Fact]
    public async Task DiscoveryPreservesWorkspaceProvenanceInsteadOfOnlyReturningTheUnion()
    {
        var client = new Client
        {
            QueryHandler = (workspace, _) => Task.FromResult(CatalogResponse(workspace.Key == "first"
                ? [["Perf"], ["Shared"], ["Perf"]] : [["Event"], ["Shared"]]))
        };
        var secret = new Secret();
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true, Workspaces = [new() { Key = "first" }, new() { Key = "second" }]
        };
        var catalogs = await new FreeKqlQueryService(options, secret, client)
            .GetWorkspaceTablesAsync(["FIRST", "second", "first"], default);
        Assert.Equal(["first", "second"], catalogs.Select(catalog => catalog.WorkspaceKey));
        Assert.Equal(["Perf", "Shared"], catalogs[0].TableNames);
        Assert.Equal(["Event", "Shared"], catalogs[1].TableNames);
        Assert.Equal(["first", "second"], client.WorkspaceKeys);
        Assert.Equal(1, secret.Calls);
    }

    [Fact]
    public async Task DiscoveryDoesNotApplyTheRawResultDisplayRowLimit()
    {
        var client = new Client { Result = CatalogResponse(Enumerable.Range(0, 1005)
            .Select(index => new object[] { $"Table_{index}" }).ToArray()) };
        var names = await new FreeKqlQueryService(Options(), new Secret(), client)
            .GetTableNamesAsync(["workspace"], default);
        Assert.Equal(1005, names.Count);
    }

    [Fact]
    public async Task EmptyDiscoverySelectionDoesNotRequireLiveConfigurationOrCredentials()
    {
        var client = new Client();
        var secret = new Secret();
        Assert.Empty(await new FreeKqlQueryService(Options(false), secret, client).GetTableNamesAsync([], default));
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, secret.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveryValidatesAllWorkspacesBeforeRequestingCredentials(bool enabled)
    {
        var client = new Client();
        var secret = new Secret();
        await Assert.ThrowsAsync<AnalysisValidationException>(() =>
            new FreeKqlQueryService(Options(enabled), secret, client)
                .GetTableNamesAsync(["workspace", "missing"], default));
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, secret.Calls);
    }

    [Theory]
    [InlineData("""{"tables":[]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"Value","type":"string"}],"rows":[]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"$table","type":"string"}],"rows":[[""]]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"$table","type":"string"}],"rows":[[null]]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"$table","type":"long"}],"rows":[[42]]}]}""")]
    [InlineData("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"$table","type":"string"}],"rows":[["Perf"]]}],"error":{"code":"PartialError","message":"Incomplete"}}""")]
    public async Task DiscoveryRejectsMalformedOrPartialResults(string json)
    {
        var client = new Client { Result = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(json))! };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FreeKqlQueryService(Options(), new Secret(), client).GetTableNamesAsync(["workspace"], default));
    }

    [Fact]
    public async Task DiscoveryReturnsAnEmptyCatalogWhenNoTablesHaveData()
    {
        var client = new Client { Result = CatalogResponse([]) };
        Assert.Empty(await new FreeKqlQueryService(Options(), new Secret(), client)
            .GetTableNamesAsync(["workspace"], default));
    }

    [Fact]
    public async Task DiscoveryPropagatesFailuresAndHonorsCancellationEvenIfTheClientIgnoresIt()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Client
        {
            QueryHandler = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(CatalogResponse([["Perf"]]));
            }
        };
        var service = new FreeKqlQueryService(Options(), new Secret(), client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetTableNamesAsync(["workspace"], cancellation.Token));
        var failure = new InvalidOperationException("Request failed");
        client.Failure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetTableNamesAsync(["workspace"], default)));
    }

    private static LogsQueryResult CatalogResponse(object[][] rows) =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            tables = new[] { new { name = "PrimaryResult",
                columns = new[] { new { name = "Count", type = "long" }, new { name = "$table", type = "string" } },
                rows = rows.Select(row => new object[] { 1L, row[0] }).ToArray() } }
        })))!;

    [Fact]
    public async Task RowBudgetIsSharedAcrossAllReturnedTables()
    {
        var tables = new[] { "First", "Second" }.Select(name => new
        {
            name, columns = new[] { new { name = "Value", type = "string" } },
            rows = Enumerable.Range(0, 600).Select(i => new[] { i.ToString() }).ToArray()
        }).ToArray();
        var client = new Client
        {
            Result = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new { tables })))!
        };
        var result = await new FreeKqlQueryService(Options(), new Secret(), client).ExecuteAsync("workspace", "print Value=1", default);
        Assert.True(result.Limited);
        Assert.Equal(2, result.Tables.Count);
        Assert.Equal(600, result.Tables[0].Rows.Count);
        Assert.Equal(400, result.Tables[1].Rows.Count);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("workspace")]
    [InlineData("empty")]
    public async Task InvalidInputsNeverFetchCredentialsOrCallAzure(string fault)
    {
        var client = new Client();
        var secret = new Secret();
        var service = new FreeKqlQueryService(Options(fault != "disabled"), secret, client);
        Assert.NotNull(await Record.ExceptionAsync(() => service.ExecuteAsync(
            fault == "workspace" ? "missing" : "workspace", fault == "empty" ? "" : "print Value=1", default)));
        Assert.Equal(0, secret.Calls);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PartialAzureFailureNeverReturnsSuccessShapedTables()
    {
        var client = new Client
        {
            Result = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
                """{"tables":[{"name":"PrimaryResult","columns":[{"name":"Value","type":"long"}],"rows":[[1]]}],"error":{"code":"PartialError","message":"private provider detail"}}"""))!
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FreeKqlQueryService(Options(), new Secret(), client).ExecuteAsync("workspace", "print Value=1", default));
        Assert.Contains("PartialError", exception.Message);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RawTableKeepsStatusColumnsAndEscapesCustomCells(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<RawQueryTableView>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { ["Tables"] = new AnalysisQueryTable[]
                    {
                        new("Original workspace", [new("Status__Status", "string")], [new string?[] { "<script>Original</script>" }])
                    } }));
                return root.ToHtmlString();
            });
            Assert.Contains("Status__Status", html);
            Assert.Contains("&lt;script&gt;Original&lt;/script&gt;", html);
            Assert.DoesNotContain("analysis-status-", html);
            Assert.Equal("OperatorAccess", Assert.Single(typeof(FreeKql).GetCustomAttributes<AuthorizeAttribute>()).Policy);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private sealed class Secret : ILogAnalyticsSecretProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetClientSecretAsync(CancellationToken token) { Calls++; token.ThrowIfCancellationRequested(); return Task.FromResult("test-secret"); }
    }
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task FreeQueryPageRendersLocalizedInputsAndSafeSuccessFailureAndCancellation(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var client = new Client { Result = Response([["Original <cell> &"]]) };
            var service = new FreeKqlQueryService(Options(), new Secret(), client);
            var activator = new PageActivator();
            await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
                .AddSingleton(Options()).AddSingleton(service).AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
            await using IAsyncDisposable rendererLifetime = new PageRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var renderer = Assert.IsType<PageRenderer>(rendererLifetime);
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var rendered = renderer.BeginRenderingComponent(typeof(FreeKql), ParameterView.Empty);
                await rendered.QuiescenceTask;
                return rendered;
            });
            var page = Assert.IsType<FreeKql>(activator.Page);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(FreeKql).GetField(name, flags)!.SetValue(page, value);
            async Task Invoke()
            {
                await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    await (Task)typeof(FreeKql).GetMethod("ExecuteAsync", flags)!.Invoke(page, [])!;
                    typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                });
            }
            var text = services.GetRequiredService<UiText>();
            var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.Contains(System.Net.WebUtility.HtmlEncode(text["FreeKql.Title"]), html);
            Assert.Contains("<details class=\"free-kql-examples\">", html);
            Assert.Contains($"<summary>{System.Net.WebUtility.HtmlEncode(text["FreeKql.Examples.Title"])}</summary>", html);
            Assert.DoesNotContain("<details class=\"free-kql-examples\" open", html);
            foreach (var key in new[] { "Title", "Tables", "Schema", "Values", "Distinct", "ExpandRows", "ExpandColumns", "Join" })
                Assert.Contains(System.Net.WebUtility.HtmlEncode(text["FreeKql.Examples." + key]), html);
            foreach (var example in new[]
            {
                "search *\n| summarize count() by $table\n| sort by $table",
                "Perf | getschema", "Perf | take 10", "Event | distinct EventID"
            })
                Assert.Contains($"<code>{example}</code>", System.Net.WebUtility.HtmlDecode(html).ReplaceLineEndings("\n"));
            foreach (var example in new[]
            {
                """
                DCDiagData_CL
                | summarize arg_max(TimeGenerated, Status) by Computer
                | extend Tests = todynamic(Status)
                | mv-expand Test = bag_keys(Tests)
                | extend TestName = tostring(Test)
                | project Timestamp = TimeGenerated, Computer, TestName,Result = tostring(Tests[TestName])
                | order by Result asc, Computer, TestName asc
                """,
                """
                DCDiagData_CL
                | summarize arg_max(TimeGenerated, Status) by Computer
                | project Computer, Timestamp = TimeGenerated, Tests = todynamic(Status)
                | evaluate bag_unpack(Tests, "")
                | order by Computer asc
                """,
                """
                let ReplData = DCReplicationData_CL
                | summarize arg_max(TimeGenerated, *) by Computer = SourceDSA, DestDSA, NC
                | where Source == "showrepl_INFO"
                | summarize
                    NumberOfFailures = sum(tolong(NumberOfFailures)),
                    LastObservation = max(TimeGenerated)
                    by Computer
                | order by NumberOfFailures desc, Computer asc;
                DCDiagData_CL
                | summarize arg_max(TimeGenerated, Status) by Computer
                | project Computer, Timestamp = TimeGenerated, Tests = todynamic(Status)
                | evaluate bag_unpack(Tests, "")
                | order by Computer asc
                | join ReplData on Computer
                """
            })
                Assert.Contains($"<code>{example.ReplaceLineEndings("\n")}</code>",
                    System.Net.WebUtility.HtmlDecode(html).ReplaceLineEndings("\n"));
            Assert.Equal("", typeof(FreeKql).GetField("kql", flags)!.GetValue(page));
            Assert.Equal(0, client.Calls);
            Assert.DoesNotContain("TimeRange", html);
            Assert.DoesNotContain("_SelectedSystems", html);
            await Invoke();
            Assert.Equal(text["FreeKql.Required"], typeof(FreeKql).GetField("error", flags)!.GetValue(page));
            Assert.Equal(0, client.Calls);
            Set("workspaceKey", "workspace");
            Set("kql", "  print Status__Status='Original <cell> &'  ");
            await Invoke();
            Assert.Contains("Original &lt;cell&gt; &amp;", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Equal("  print Status__Status='Original <cell> &'  ", client.Kql);
            client.Failure = new InvalidOperationException("private provider failure");
            await Invoke();
            Assert.Null(typeof(FreeKql).GetField("result", flags)!.GetValue(page));
            Assert.Equal(text["FreeKql.Failed"], typeof(FreeKql).GetField("error", flags)!.GetValue(page));
            Assert.DoesNotContain("private provider failure", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            client.Failure = null;
            client.BeforeResult = token =>
            {
                typeof(FreeKql).GetMethod("Cancel", flags)!.Invoke(page, []);
                token.ThrowIfCancellationRequested();
            };
            await Invoke();
            Assert.Equal(text["FreeKql.Cancelled"], typeof(FreeKql).GetField("error", flags)!.GetValue(page));
            Assert.False(Assert.IsType<bool>(typeof(FreeKql).GetField("busy", flags)!.GetValue(page)));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task WorkspaceTablesAppearBelowSelectionAndShareOneDiscoveryWithCompletion(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var client = new Client { Result = CatalogResponse([["ZTable"], ["Perf"], ["Perf"], ["<Table &>"]]) };
        await using var page = new WorkspacePage(client);
        await page.StartAsync();
        Assert.DoesNotContain("free-kql-workspace-tables", await page.HtmlAsync());
        Assert.Equal(0, client.Calls);
        await page.SelectAsync("first");
        Assert.Contains(page.Text["KqlEditor.TablesLoading"], System.Net.WebUtility.HtmlDecode(await page.HtmlAsync()));
        await page.DiscoverAsync();
        var html = await page.HtmlAsync();
        var tables = Regex.Match(html, "<section class=\"free-kql-workspace-tables admin-field-wide\".*?</section>", RegexOptions.Singleline);
        Assert.True(tables.Success);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(page.Text["FreeKql.Tables.Title"]), tables.Value);
        Assert.Contains("<code>Perf</code>", tables.Value);
        Assert.Contains("<code>ZTable</code>", tables.Value);
        Assert.Contains("<code>&lt;Table &amp;&gt;</code>", tables.Value);
        Assert.DoesNotContain("<Table &>", tables.Value);
        Assert.Equal(3, Regex.Matches(tables.Value, "<li>").Count);
        Assert.True(tables.Index > html.IndexOf("</select>", StringComparison.Ordinal));
        Assert.True(tables.Index < html.IndexOf("data-kql-editor", StringComparison.Ordinal));
        Assert.Equal(FreeKqlQueryService.TableNamesQuery, Assert.Single(client.Queries));
        var catalogJson = System.Net.WebUtility.HtmlDecode(
            Regex.Match(html, "data-kql-tables=\"([^\"]*)\"").Groups[1].Value);
        Assert.Equal(["<Table &>", "Perf", "ZTable"], Assert.IsType<string[]>(JsonSerializer.Deserialize<string[]>(catalogJson)));
        await page.SetQueryAsync("Perf | take 10");
        await page.DiscoverAsync();
        Assert.Equal(1, client.Calls);
        Assert.Equal("Perf | take 10", page.Query);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task WorkspaceTableFailureRetryEmptyAndClearAreExplicitAndPreserveTheQuery(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var response = CatalogResponse([]);
        var client = new Client
        {
            Failure = new InvalidOperationException("private metadata diagnostic"),
            QueryHandler = (_, _) => Task.FromResult(response)
        };
        await using var page = new WorkspacePage(client);
        await page.StartAsync();
        await page.SetQueryAsync("original query");
        await page.SelectAsync("first");
        await page.DiscoverAsync();
        var html = System.Net.WebUtility.HtmlDecode(await page.HtmlAsync());
        var tables = Regex.Match(html, "<section class=\"free-kql-workspace-tables admin-field-wide\".*?</section>", RegexOptions.Singleline).Value;
        Assert.Contains("role=\"alert\"", tables);
        Assert.Contains(page.Text["KqlEditor.TablesFailed"], tables);
        Assert.DoesNotContain("private metadata diagnostic", html);
        client.Failure = null;
        await page.RetryAsync();
        tables = System.Net.WebUtility.HtmlDecode(await page.HtmlAsync());
        Assert.Contains(page.Text["FreeKql.Tables.Empty"], tables);
        Assert.DoesNotContain(page.Text["KqlEditor.TablesFailed"], tables);
        response = CatalogResponse([["Ready"]]);
        await page.RetryAsync();
        Assert.Contains("<code>Ready</code>", await page.HtmlAsync());
        Assert.Equal(3, client.Calls);
        await page.SelectAsync("");
        await page.DiscoverAsync();
        Assert.DoesNotContain("free-kql-workspace-tables", await page.HtmlAsync());
        var cleared = await page.HtmlAsync();
        Assert.DoesNotContain("<code>Ready</code>", cleared);
        var clearedCatalog = System.Net.WebUtility.HtmlDecode(
            Regex.Match(cleared, "data-kql-tables=\"([^\"]*)\"").Groups[1].Value);
        Assert.Empty(JsonSerializer.Deserialize<string[]>(clearedCatalog)!);
        Assert.Equal(3, client.Calls);
        Assert.Equal("original query", page.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("second")]
    public async Task WorkspaceTableListNeverShowsLateResultsFromAPreviousSelection(string next)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<LogsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken previousToken = default;
        var client = new Client
        {
            QueryHandler = (workspace, token) =>
            {
                if (workspace.Key != "first") return Task.FromResult(CatalogResponse([["CurrentTable"]]));
                previousToken = token;
                started.TrySetResult();
                return pending.Task;
            }
        };
        await using var page = new WorkspacePage(client);
        await page.StartAsync();
        await page.SelectAsync("first");
        var discovery = page.DiscoverAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.SelectAsync(next);
            Assert.True(previousToken.IsCancellationRequested);
            await page.DiscoverAsync();
        }
        finally
        {
            pending.TrySetResult(CatalogResponse([["ObsoleteTable"]]));
            await discovery;
        }
        var html = await page.HtmlAsync();
        Assert.DoesNotContain("ObsoleteTable", html);
        if (next == "") Assert.DoesNotContain("free-kql-workspace-tables", html);
        else Assert.Contains("<code>CurrentTable</code>", html);
        Assert.Equal(next == "" ? 1 : 2, client.Calls);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task EditorSpansTheFormWidthAndExecutionButtonsStayInTheirOwnActionRow(string culture, bool busy)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var page = new WorkspacePage(new Client { Result = CatalogResponse([["Perf"]]) });
        await page.StartAsync();
        await page.SelectAsync("first");
        await page.DiscoverAsync();
        await page.SetBusyAsync(busy);
        var html = await page.HtmlAsync();
        Assert.Matches("<label class=\"admin-field-wide\">\\s*KQL\\s*<span[^>]*data-kql-editor", html);
        var actions = Regex.Match(html, "<div class=\"admin-form-actions free-kql-actions\">(.*?)</div>", RegexOptions.Singleline);
        Assert.True(actions.Success);
        Assert.Equal(busy ? 2 : 1, Regex.Matches(actions.Value, "<button").Count);
        Assert.Contains("type=\"submit\"", actions.Value);
        Assert.Contains(page.Text[busy ? "FreeKql.Running" : "FreeKql.Execute"], System.Net.WebUtility.HtmlDecode(actions.Value));
        if (busy)
        {
            Assert.Contains("type=\"button\"", actions.Value);
            Assert.Contains(page.Text["Common.Cancel"], System.Net.WebUtility.HtmlDecode(actions.Value));
        }
        Assert.True(actions.Index > html.IndexOf("data-kql-editor", StringComparison.Ordinal));
    }

    private sealed class WorkspacePage : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PageActivator activator = new();
        private readonly ServiceProvider services;
        private readonly PageRenderer renderer;
        private Func<string>? html;
        public WorkspacePage(Client client)
        {
            var options = new CrossTenantLogAnalyticsOptions
            {
                Enabled = true, ApplicationClientId = "app",
                Workspaces = [new() { Key = "first" }, new() { Key = "second" }]
            };
            services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
                .AddSingleton(options).AddSingleton(new FreeKqlQueryService(options, new Secret(), client))
                .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
            renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public UiText Text => services.GetRequiredService<UiText>();
        public string Query => Assert.IsType<string>(typeof(FreeKql).GetField("kql", Flags)!.GetValue(activator.Page));
        public Task StartAsync() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(FreeKql), ParameterView.Empty);
            await root.QuiescenceTask;
            html = root.ToHtmlString;
        });
        public Task SelectAsync(string key) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(FreeKql).GetField("workspaceKey", Flags)!.SetValue(activator.Page, key);
            typeof(FreeKql).GetMethod("WorkspaceChanged", Flags)!.Invoke(activator.Page, []);
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(activator.Page, []);
        });
        public Task SetQueryAsync(string query) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(FreeKql).GetField("kql", Flags)!.SetValue(activator.Page, query);
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(activator.Page, []);
        });
        public Task SetBusyAsync(bool value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(FreeKql).GetField("busy", Flags)!.SetValue(activator.Page, value);
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(activator.Page, []);
        });
        public Task DiscoverAsync() => renderer.Dispatcher.InvokeAsync(() =>
            (Task)typeof(Components.KQL.KqlEditor).GetMethod("OnAfterRenderAsync", Flags)!.Invoke(activator.Editor, [false])!);
        public async Task RetryAsync()
        {
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                typeof(Components.KQL.KqlEditor).GetMethod("RetryTables", Flags)!.Invoke(activator.Editor, []);
                typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(activator.Editor, []);
            });
            await DiscoverAsync();
        }
        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(() => html!());
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class PageActivator : IComponentActivator
    {
        public FreeKql? Page { get; private set; }
        public Components.KQL.KqlEditor? Editor { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is FreeKql page) Page = page;
            if (component is Components.KQL.KqlEditor editor) Editor = editor;
            return component;
        }

    }

#pragma warning disable BL0006
    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory logger)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, logger)
    {
        protected override IComponent ResolveComponentForRenderMode(Type type, int? parent,
            IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type) : base.ResolveComponentForRenderMode(type, parent, activator, mode);
    }
#pragma warning restore BL0006

    private sealed class Client : IFreeKqlQueryClient
    {
        public LogsQueryResult Result { get; init; } = Response([]);
        public Func<CrossTenantLogAnalyticsWorkspaceOptions, CancellationToken, Task<LogsQueryResult>>? QueryHandler { get; init; }
        public List<string> WorkspaceKeys { get; } = [];
        public List<string> Queries { get; } = [];
        public string? Kql { get; private set; }
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Action<CancellationToken>? BeforeResult { get; set; }
        public Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace,
            string applicationClientId, string secret, string kql, CancellationToken token)
        {
            Calls++; Kql = kql; WorkspaceKeys.Add(workspace.Key); Queries.Add(kql); token.ThrowIfCancellationRequested();
            BeforeResult?.Invoke(token);
            if (Failure is not null) return Task.FromException<LogsQueryResult>(Failure);
            return QueryHandler?.Invoke(workspace, token) ?? Task.FromResult(Result);
        }
    }
}
