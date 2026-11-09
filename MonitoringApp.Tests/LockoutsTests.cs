using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class LockoutsTests
{
    private static CrossTenantLogAnalyticsOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled, ApplicationClientId = "test-app",
        Workspaces = [new() { Key = "first", DisplayName = "First workspace" }, new() { Key = "second", DisplayName = "Second workspace" }]
    };

    private static LogsQueryResult Response(string[] columns, object?[][] rows) =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            tables = new[] { new { name = "PrimaryResult", columns = columns.Select(name => new { name, type = "string" }), rows } }
        })))!;

    private static LogsQueryResult Users(params string[] users) => Response(["User"], users.Select(user => new object?[] { user }).ToArray());

    [Theory]
    [InlineData(60)]
    [InlineData(240)]
    [InlineData(720)]
    [InlineData(1440)]
    public async Task BothQueriesUseSelectedWorkspaceAndExactTimeWindow(int minutes)
    {
        var client = new Client { Result = Users("Zulu", "Alice", "Alice", "") };
        var secrets = new Secret();
        var service = new LockoutQueryService(Options(), secrets, client);
        var users = await service.GetUsersAsync("SECOND", minutes, default);
        Assert.Equal(new[] { "Alice", "Zulu" }, users.Names);
        Assert.False(users.Limited);
        Assert.Equal("second", client.Requests[0].Workspace.Key);
        Assert.Equal(minutes, client.Requests[0].Minutes);
        Assert.Equal("Event\n| where EventID == 4740\n| extend User = parse_xml(ParameterXml).Param[0]\n| distinct tostring(User)", client.Requests[0].Kql);
        client.Result = Response(["TimeGenerated", "EventID", "User", "Protocol", "Client", "ClientIP", "Process"],
            [["2026-11-09T08:00:00Z", "4625", "Alice", "NTLM", "Client", "10.0.0.1", "Process"]]);
        var result = await service.SearchAsync("second", minutes, "Alice", default);
        Assert.Equal("second", client.Requests[1].Workspace.Key);
        Assert.Equal(minutes, client.Requests[1].Minutes);
        Assert.Equal(LockoutQueryTemplates.Events("Alice"), client.Requests[1].Kql);
        Assert.Contains("PXML.Param[5] == _LockoutUser", client.Requests[1].Kql);
        Assert.Equal(new[] { "TimeGenerated", "EventID", "User", "Protocol", "Client", "ClientIP", "Process" },
            Assert.Single(result.Tables).Columns.Select(column => column.Name));
        Assert.Equal("Alice", Assert.Single(Assert.Single(result.Tables).Rows)[2]);
        Assert.Equal(2, secrets.Calls);
    }

    [Theory]
    [InlineData("DOMAIN\\alice")]
    [InlineData("alice\"; Event | take 100 //")]
    [InlineData("alice'\n| union Event\r\n\\")]
    [InlineData("Älice <&>")]
    public void UserParameterRoundTripsAsOneStringLiteralWithoutChangingTheQuery(string user)
    {
        var kql = LockoutQueryTemplates.Events(user);
        var firstLine = kql[..kql.IndexOf('\n')];
        var literal = firstLine["let _LockoutUser = ".Length..^1];
        Assert.Equal(user, JsonSerializer.Deserialize<string>(literal));
        Assert.DoesNotContain('\n', firstLine);
        Assert.DoesNotContain("{{User}}", kql);
        Assert.Equal(LockoutQueryTemplates.Events("reference")[(LockoutQueryTemplates.Events("reference").IndexOf('\n') + 1)..],
            kql[(kql.IndexOf('\n') + 1)..]);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("workspace")]
    [InlineData("window")]
    [InlineData("user")]
    public async Task InvalidSelectionsNeverCallAzureOrFetchSecrets(string fault)
    {
        var secrets = new Secret();
        var client = new Client();
        var service = new LockoutQueryService(Options(fault != "disabled"), secrets, client);
        var workspace = fault == "workspace" ? "unknown" : "first";
        var minutes = fault == "window" ? 480 : 60;
        Assert.NotNull(await Record.ExceptionAsync(() => service.SearchAsync(workspace, minutes, fault == "user" ? "" : "Alice", default)));
        if (fault != "user")
            Assert.NotNull(await Record.ExceptionAsync(() => service.GetUsersAsync(workspace, minutes, default)));
        Assert.Empty(client.Requests);
        Assert.Equal(0, secrets.Calls);
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public async Task UsersAndSearchShareTheExistingRowLimit(int rows, bool limited)
    {
        var client = new Client { Result = Users(Enumerable.Range(0, rows).Select(index => $"User{index}").ToArray()) };
        var service = new LockoutQueryService(Options(), new Secret(), client);
        var users = await service.GetUsersAsync("first", 60, default);
        Assert.Equal(limited, users.Limited);
        Assert.Equal(Math.Min(rows, 1000), users.Names.Count);
        var result = await service.SearchAsync("first", 60, "User1", default);
        Assert.Equal(limited, result.Limited);
        Assert.Equal(Math.Min(rows, 1000), Assert.Single(result.Tables).Rows.Count);
    }

    [Fact]
    public async Task PartialFailuresAndWrongUserSchemaAreNotEmptySuccesses()
    {
        var client = new Client
        {
            Result = ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
                """{"tables":[{"name":"PrimaryResult","columns":[{"name":"User","type":"string"}],"rows":[["Alice"]]}],"error":{"code":"PartialError","message":"private provider details"}}"""))!
        };
        var service = new LockoutQueryService(Options(), new Secret(), client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetUsersAsync("first", 60, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("first", 60, "Alice", default));
        client.Result = Response(["Unexpected", "Extra"], [["Alice", "Other"]]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetUsersAsync("first", 60, default));
    }

    [Theory]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Reader)]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Operator)]
    [InlineData(ApplicationAuthenticationOptions.Sql, SqlAuthenticationRoles.Admin)]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Reader)]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Operator)]
    [InlineData(ApplicationAuthenticationOptions.Entra, SqlAuthenticationRoles.Admin)]
    public async Task ApplicationPolicyAllowsAllThreeRolesButNotAnonymous(string mode, string role)
    {
        var attribute = Assert.Single(typeof(Lockouts).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(ApplicationAuthorization.AccessPolicy, attribute.Policy);
        await using var services = new ServiceCollection().AddLogging().AddAuthorization(options =>
            ApplicationAuthorization.Configure(options, new() { Type = mode })).BuildServiceProvider();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role, role)], "test"));
        Assert.True((await authorization.AuthorizeAsync(user, null, attribute.Policy!)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, attribute.Policy!)).Succeeded);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PageLoadsUsersSearchesAndResetsSelectionsWhenWorkspaceOrWindowChanges(string culture)
    {
        using var selection = new CultureSelection(culture);
        await using var fixture = new Fixture();
        const string name = "Alice <&> \"";
        fixture.Client.Result = Users(name, "Bob");
        await fixture.Render();
        var html = await fixture.Html();
        foreach (var key in new[] { "Lockouts.Title", "Lockouts.Help", "Lockouts.TimeRange", "Lockouts.User", "Lockouts.Search",
            "Lockouts.TimeRange.60", "Lockouts.TimeRange.240", "Lockouts.TimeRange.720", "Lockouts.TimeRange.1440" })
            Assert.Contains(fixture.Text[key], WebUtility.HtmlDecode(html));
        Assert.Contains(WebUtility.HtmlEncode(name), html);
        Assert.Single(fixture.Client.Requests);
        await fixture.Invoke("SelectUser", name);
        fixture.Client.Result = Response(["TimeGenerated", "EventID", "User", "Protocol", "Client", "ClientIP", "Process"],
            [["2026-11-09T08:00:00Z", "4625", name, "NTLM", "<script>client</script>", "10.0.0.1", "Process"]]);
        await fixture.Invoke("SearchAsync");
        Assert.NotNull(fixture.Field("result"));
        html = await fixture.Html();
        Assert.Contains("&lt;script&gt;client&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>client</script>", html);
        fixture.Set("timeRangeMinutes", 720);
        fixture.Client.Result = Users("New user");
        await fixture.Invoke("LoadUsersAsync");
        Assert.Equal("", fixture.Field("selectedUser"));
        Assert.Null(fixture.Field("result"));
        Assert.Equal(720, fixture.Client.Requests[^1].Minutes);
        Assert.DoesNotContain(name, WebUtility.HtmlDecode(await fixture.Html()));
        fixture.Set("workspaceKey", "second");
        await fixture.Invoke("LoadUsersAsync");
        Assert.Equal("second", fixture.Client.Requests[^1].Workspace.Key);
        var calls = fixture.Client.Requests.Count;
        await fixture.Invoke("SelectUser", "not-from-current-list");
        await fixture.Invoke("SearchAsync");
        Assert.Equal(calls, fixture.Client.Requests.Count);
        Assert.Equal(fixture.Text["Lockouts.SelectUser"], fixture.Field("error"));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PageSurfacesEmptyResultsFailuresAndCancellation(string culture)
    {
        using var selection = new CultureSelection(culture);
        await using var fixture = new Fixture();
        fixture.Client.Result = Users();
        await fixture.Render();
        Assert.Contains(fixture.Text["Lockouts.NoUsers"], WebUtility.HtmlDecode(await fixture.Html()));
        fixture.Client.Result = Users("Alice");
        await fixture.Invoke("LoadUsersAsync");
        await fixture.Invoke("SelectUser", "Alice");
        fixture.Client.Result = Response(["User"], []);
        await fixture.Invoke("SearchAsync");
        Assert.Contains(fixture.Text["Lockouts.NoEvents"], WebUtility.HtmlDecode(await fixture.Html()));
        fixture.Client.Failure = new InvalidOperationException("private diagnostic");
        await fixture.Invoke("SearchAsync");
        Assert.Equal(fixture.Text["Lockouts.SearchFailed"], fixture.Field("error"));
        Assert.DoesNotContain("private diagnostic", await fixture.Html());
        Assert.Null(fixture.Field("result"));
        fixture.Client.Failure = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.OnQuery = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Users();
        };
        var pending = fixture.Invoke("SearchAsync");
        await started.Task;
        await fixture.Invoke("Cancel");
        await pending;
        Assert.Equal(fixture.Text["FreeKql.Cancelled"], fixture.Field("error"));
        Assert.Null(fixture.Field("result"));
        Assert.Equal(false, fixture.Field("busy"));
    }

    [Fact]
    public async Task OldUserResponseCannotOverwriteNewWorkspaceOrClearItsBusyState()
    {
        await using var fixture = new Fixture();
        await fixture.Render();
        var oldResponse = new TaskCompletionSource<LogsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResponse = new TaskCompletionSource<LogsQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.OnQuery = token => { oldStarted.SetResult(); return oldResponse.Task; };
        var old = fixture.Invoke("LoadUsersAsync");
        await oldStarted.Task;
        fixture.Set("workspaceKey", "second");
        fixture.Client.OnQuery = token => { newStarted.SetResult(); return newResponse.Task; };
        var current = fixture.Invoke("LoadUsersAsync");
        await newStarted.Task;
        oldResponse.SetResult(Users("Old user"));
        await old;
        Assert.Equal(true, fixture.Field("busy"));
        newResponse.SetResult(Users("Current user"));
        await current;
        Assert.Equal(new[] { "Current user" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(fixture.Field("users")));
        Assert.Equal(false, fixture.Field("busy"));
    }

    [Fact]
    public async Task DisabledLiveQueriesDoNotLoadUsersOrCallAzure()
    {
        await using var fixture = new Fixture(enabled: false);
        await fixture.Render();
        Assert.Empty(fixture.Client.Requests);
        Assert.Contains(fixture.Text["Analysis.Validation.LiveDisabled"], WebUtility.HtmlDecode(await fixture.Html()));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task UserLoadFailureIsVisibleAndRetryClearsItWithoutAnEmptySuccess(string culture)
    {
        using var selection = new CultureSelection(culture);
        await using var fixture = new Fixture();
        fixture.Client.Failure = new InvalidOperationException("private provider diagnostic");
        await fixture.Render();
        Assert.Equal(fixture.Text["Lockouts.LoadFailed"], fixture.Field("error"));
        Assert.Equal(false, fixture.Field("usersLoaded"));
        var html = WebUtility.HtmlDecode(await fixture.Html());
        Assert.DoesNotContain(fixture.Text["Lockouts.NoUsers"], html);
        Assert.DoesNotContain("private provider diagnostic", html);
        fixture.Client.Failure = null;
        await fixture.Invoke("LoadUsersAsync");
        Assert.Null(fixture.Field("error"));
        Assert.Equal(true, fixture.Field("usersLoaded"));
    }

    [Fact]
    public async Task ByteLimitedEmptyResponsesShowLimitInsteadOfClaimingNoEventsOrUsers()
    {
        await using var fixture = new Fixture();
        fixture.Client.Result = Users(new string('x', KqlQueryLimits.MaxTableResponseBytes));
        await fixture.Render();
        var html = WebUtility.HtmlDecode(await fixture.Html());
        Assert.Contains(fixture.Text["FreeKql.Limited"], html);
        Assert.DoesNotContain(fixture.Text["Lockouts.NoUsers"], html);
        fixture.Client.Result = Users("Alice");
        await fixture.Invoke("LoadUsersAsync");
        await fixture.Invoke("SelectUser", "Alice");
        fixture.Client.Result = Response(["Process"], [[new string('x', KqlQueryLimits.MaxTableResponseBytes)]]);
        await fixture.Invoke("SearchAsync");
        html = WebUtility.HtmlDecode(await fixture.Html());
        Assert.Contains(fixture.Text["FreeKql.Limited"], html);
        Assert.DoesNotContain(fixture.Text["Lockouts.NoEvents"], html);
    }

    private sealed class Secret : ILogAnalyticsSecretProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult("test-secret");
        }
    }

    private sealed record Request(CrossTenantLogAnalyticsWorkspaceOptions Workspace, string Kql, int Minutes);
    private sealed class Client : IAnalysisLogAnalyticsQuery
    {
        public LogsQueryResult Result { get; set; } = Users("Alice");
        public List<Request> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public Func<CancellationToken, Task<LogsQueryResult>>? OnQuery { get; set; }
        public Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId,
            string secret, string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Requests.Add(new(workspace, kql, timeRangeMinutes));
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is not null ? Task.FromException<LogsQueryResult>(Failure)
                : OnQuery?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }
    }

    private sealed class CultureSelection(string culture) : IDisposable
    {
        private readonly CultureInfo oldUi = CultureInfo.CurrentUICulture, oldFormat = CultureInfo.CurrentCulture;
        private readonly bool initialized = Initialize(culture);
        private static bool Initialize(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            return true;
        }
        public void Dispose()
        {
            if (initialized) { CultureInfo.CurrentUICulture = oldUi; CultureInfo.CurrentCulture = oldFormat; }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly ServiceProvider services;
        private readonly Renderer renderer;
        private readonly Activator activator = new();
        private Func<string>? html;
        public Client Client { get; } = new();
        public UiText Text => services.GetRequiredService<UiText>();
        private Lockouts Page => activator.Page!;
        public Fixture(bool enabled = true)
        {
            services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton(Options(enabled)).AddSingleton<ILogAnalyticsSecretProvider, Secret>()
                .AddSingleton<IAnalysisLogAnalyticsQuery>(Client).AddSingleton<LockoutQueryService>()
                .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
            renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public Task Render() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Lockouts), ParameterView.Empty);
            await root.QuiescenceTask;
            html = root.ToHtmlString;
        });
        public object? Field(string name) => typeof(Lockouts).GetField(name, Flags)!.GetValue(Page);
        public void Set(string name, object? value) => typeof(Lockouts).GetField(name, Flags)!.SetValue(Page, value);
        public Task<string> Html() => renderer.Dispatcher.InvokeAsync(() => html!());
        public Task Invoke(string name, params object?[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var operation = typeof(Lockouts).GetMethod(name, Flags)!.Invoke(Page, arguments);
            if (operation is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Page, []);
        });
        public async ValueTask DisposeAsync()
        {
            activator.Page?.Dispose();
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class Activator : IComponentActivator
    {
        public Lockouts? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is Lockouts page) Page = page;
            return component;
        }
    }

#pragma warning disable BL0006
    private sealed class Renderer(IServiceProvider services, ILoggerFactory logger)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, logger)
    {
        protected override IComponent ResolveComponentForRenderMode(Type type, int? parent,
            IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type) : base.ResolveComponentForRenderMode(type, parent, activator, mode);
    }
#pragma warning restore BL0006
}
