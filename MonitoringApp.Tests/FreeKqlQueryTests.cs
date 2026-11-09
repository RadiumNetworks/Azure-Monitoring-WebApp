using System.ClientModel.Primitives;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
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
            await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
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
            foreach (var key in new[] { "Title", "Tables", "Schema", "Values", "Distinct", "ExpandRows", "ExpandColumns", "Join" })
                Assert.Contains(System.Net.WebUtility.HtmlEncode(text["FreeKql.Examples." + key]), html);
            foreach (var example in new[]
            {
                "search *\n| summarize count() by $table\n| sort by $table",
                "Perf | getschema", "Perf | take 10", "Event | distinct EventID"
            })
                Assert.Contains($"<code>{example}</code>", html.Replace("\r\n", "\n"));
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

    private sealed class PageActivator : IComponentActivator
    {
        public FreeKql? Page { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is FreeKql page) Page = page;
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
        public string? Kql { get; private set; }
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Action<CancellationToken>? BeforeResult { get; set; }
        public Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace,
            string applicationClientId, string secret, string kql, CancellationToken token)
        {
            Calls++; Kql = kql; token.ThrowIfCancellationRequested();
            BeforeResult?.Invoke(token);
            if (Failure is not null) return Task.FromException<LogsQueryResult>(Failure);
            return Task.FromResult(Result);
        }
    }
}
