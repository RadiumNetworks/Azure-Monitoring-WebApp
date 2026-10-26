using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardPublishingTests
{
    private static readonly ClaimsPrincipal Alice = User("alice@example.com", SqlAuthenticationRoles.Admin);
    private static readonly ClaimsPrincipal Bob = User("bob@example.com", SqlAuthenticationRoles.Admin);
    private static readonly ClaimsPrincipal Operator = User("operator@example.com", SqlAuthenticationRoles.Operator);

    [Fact]
    public async Task PublishesSavedSnapshotWithoutSharingPersonalLibraryPreferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        var saved = await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        Assert.Empty(await fixture.Publishing.ListAsync(Operator));

        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, saved.Revision, Alice);
        Assert.Equal(1, publication.Version);
        Assert.Equal("alice@example.com", publication.Owner);
        var snapshot = await fixture.Publishing.LoadAsync(publication.Id, Operator);
        Assert.Equal("AD dashboard", snapshot.Document.Name);
        Assert.Equal("DC01", Assert.Single(snapshot.Document.Systems).Computer);
        Assert.Equal(240, Assert.Single(snapshot.Document.Widgets).TimeRangeMinutes);
        Assert.Empty(snapshot.Document.HiddenQueryIds);
        Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.HiddenQueryIds);
        Assert.Null((await fixture.Repository.LoadAsync("operator@example.com")).Collection);
        Assert.Single(await fixture.Publishing.ListAsync(Bob));

        snapshot.Document.Widgets.Clear();
        Assert.Single((await fixture.Publishing.LoadAsync(publication.Id, Operator)).Document.Widgets);
    }

    [Fact]
    public async Task PersonalEditsRemainPrivateUntilRepublishedAndRevisionIsProtected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var stored = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal(2, stored.Revision);
        stored.Collection!.RenameActive("Private edit");
        stored.Collection.Active.Document.Systems[0].Computer = "DC02";
        await fixture.Repository.SaveAsync("alice@example.com", stored.Collection, stored.Revision);
        var original = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal("AD dashboard", original.Document.Name);
        Assert.Equal("DC01", original.Document.Systems[0].Computer);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 2, Alice));
        var republished = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 3, Alice);
        Assert.Equal(2, republished.Version);
        var updated = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal("Private edit", updated.Document.Name);
        Assert.Equal("DC02", updated.Document.Systems[0].Computer);
        Assert.Equal(4, (await fixture.Repository.LoadAsync("alice@example.com")).Revision);
    }

    [Fact]
    public async Task AnalysisPageDefaultsToCompactViewWithExistingDashboardsSystemsAndWidgets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            (await context.AnalysisDashboardTemplates.SingleAsync()).Version = 7;
            var query = ActiveDirectoryAnalysisQueries.Create().First();
            query.Id = Guid.NewGuid();
            query.Versions.Add(new()
            {
                Id = Guid.NewGuid(), Version = 4,
                SnapshotJson = System.Text.Json.JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
            });
            query.PublishedVersionId = query.Versions.Single().Id;
            context.AnalysisQueryDefinitions.Add(query);
            await context.SaveChangesAsync();
        }
        var options = new CrossTenantLogAnalyticsOptions();
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var configuration = new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder());
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(runner)
            .AddSingleton(new AlertStore(fixture.Factory, configuration, presenter, new AlertRuleEvaluator(presenter),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton(new AnalysisDashboardState())
            .AddSingleton(fixture.Repository)
            .AddSingleton(options)
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new UnusedJavaScript())
            .BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        RenderFragment child = builder =>
        {
            builder.OpenComponent<MonitoringApp.Components.Pages.Analysis>(0);
            builder.CloseComponent();
        };
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                    ["ChildContent"] = child
                }));
            await rendered.QuiescenceTask;
            return rendered.ToHtmlString();
        });
        html = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains($"{publication.Name} · {publication.Owner} · v7", html);
        Assert.Contains("<span>Edit</span>", html);
        Assert.Contains("Select dashboard", html);
        Assert.Contains("Systems", html);
        Assert.Contains("LDAP latency", html);
        Assert.DoesNotContain("Published v7 · personal edits remain private", html);
        Assert.DoesNotContain("Query library", html);
        Assert.DoesNotContain("Published query", html);
        Assert.DoesNotContain("Execution preview", html);
        Assert.DoesNotContain("Create dashboard", html);
        Assert.DoesNotContain("Run dashboard", html);
        Assert.DoesNotContain("Save dashboard", html);
        Assert.DoesNotContain("Publish updated dashboard", html);
        Assert.DoesNotContain("data-inbox-drag-handle", html);
        Assert.DoesNotContain("data-inbox-resize-handle", html);
        Assert.DoesNotContain("data-analysis-panel-resize", html);
        Assert.DoesNotContain("@publication.Version", html);
        Assert.DoesNotContain("@OwnPublication.Version", html);
        Assert.DoesNotContain("@query.Version", html);
    }

    [Fact]
    public async Task CompactPublishedViewKeepsSystemsAndWidgetsWithoutExecutionControls()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var options = new CrossTenantLogAnalyticsOptions { Enabled = false };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(options).AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Operator)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent<PublishedAnalysisDashboardView>(0);
                        builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                        builder.AddAttribute(2, "ShowControls", false);
                        builder.AddAttribute(3, "AutoRunOnOpen", true);
                        builder.CloseComponent();
                    })
                }))).ToHtmlString());
        Assert.Contains("LDAP latency", html);
        Assert.Contains("DC01", html);
        Assert.Contains("Live Log Analytics is disabled", html);
        Assert.DoesNotContain("Run dashboard", html);
        Assert.DoesNotContain("Auto-refresh", html);
        Assert.DoesNotContain("Dashboard time range", html);
        Assert.DoesNotContain("Dashboard averaging interval", html);
    }

    [Fact]
    public async Task PublishesExecutionDefaultsWhileViewerChangesRemainSessionLocal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        collection.Active.Document.TimeRangeMinutesOverride = 10080;
        collection.Active.Document.BinMinutesOverride = 30;
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var snapshot = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal(10080, snapshot.Document.TimeRangeMinutesOverride);
        Assert.Equal(30, snapshot.Document.BinMinutesOverride);
        snapshot.Document.TimeRangeMinutesOverride = 60;
        snapshot.Document.BinMinutesOverride = null;
        var widget = Assert.Single(snapshot.Document.Widgets);
        Assert.Equal(60, snapshot.Document.CreateExecutionRequest(widget, []).TimeRangeMinutes);
        Assert.Equal(240, widget.TimeRangeMinutes);

        var fresh = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal(10080, fresh.Document.TimeRangeMinutesOverride);
        Assert.Equal(30, fresh.Document.BinMinutesOverride);
        var personal = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal(10080, personal.Collection!.Active.Document.TimeRangeMinutesOverride);
        Assert.Equal(30, personal.Collection.Active.Document.BinMinutesOverride);
    }

    [Fact]
    public async Task WithdrawalRevokesFreshReadsRetainsPersonalOriginalAndAllowsRepublishing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        await fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice);
        Assert.Empty(await fixture.Publishing.ListAsync(Operator));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator));
        Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Widgets);
        var republished = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 2, Alice);
        Assert.Equal(2, republished.Version);
        Assert.Single(await fixture.Publishing.ListAsync(Operator));
    }

    [Fact]
    public async Task AdminCannotPublishOrWithdrawAnotherCreatorsDashboard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var aliceCollection = CreateDashboard();
        var bobCollection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", aliceCollection, 0);
        await fixture.Repository.SaveAsync("bob@example.com", bobCollection, 0);
        await fixture.Publishing.PublishAsync(aliceCollection.ActiveDashboardId, 1, Alice);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(aliceCollection.ActiveDashboardId, 1, Bob));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.WithdrawAsync(aliceCollection.ActiveDashboardId, Bob));
        Assert.Single(await fixture.Publishing.ListAsync(Operator));
    }

    [Fact]
    public async Task OperatorCannotPublishOrWithdrawEvenTheirOwnDashboardAndReaderCannotView()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("operator@example.com", collection, 0);
        Assert.False(await fixture.Publishing.CanPublishAsync(Operator));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Operator));
        var promoted = User("operator@example.com", SqlAuthenticationRoles.Admin);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, promoted);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Operator));
        var reader = User("reader@example.com", SqlAuthenticationRoles.Reader);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.ListAsync(reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.LoadAsync(collection.ActiveDashboardId, reader));
    }

    [Fact]
    public async Task SameNamesAndVersionsAreIndependentAcrossCreators()
    {
        await using var fixture = await Fixture.CreateAsync();
        var aliceCollection = CreateDashboard();
        var bobCollection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", aliceCollection, 0);
        await fixture.Repository.SaveAsync("bob@example.com", bobCollection, 0);
        await fixture.Publishing.PublishAsync(aliceCollection.ActiveDashboardId, 1, Alice);
        await fixture.Publishing.PublishAsync(bobCollection.ActiveDashboardId, 1, Bob);
        var publications = await fixture.Publishing.ListAsync(Operator);
        Assert.Equal(2, publications.Count);
        Assert.All(publications, item => { Assert.Equal(1, item.Version); Assert.Equal("AD dashboard", item.Name); });
        await fixture.Publishing.WithdrawAsync(aliceCollection.ActiveDashboardId, Alice);
        Assert.Equal("bob@example.com", Assert.Single(await fixture.Publishing.ListAsync(Operator)).Owner);
    }

    [Fact]
    public async Task CannotDeletePublishedSourceUntilWithdrawn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        var publishedId = collection.ActiveDashboardId;
        collection.Add("Other");
        collection.Select(publishedId);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(publishedId, 1, Alice);
        collection.DeleteActive();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Repository.SaveAsync("alice@example.com", collection, 2));
        Assert.Contains("Withdraw", exception.Message);
        Assert.Equal(2, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
        await fixture.Publishing.WithdrawAsync(publishedId, Alice);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 2);
        Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.PublishAsync(publishedId, 3, Alice));
    }

    [Fact]
    public async Task OpenModeKeepsExistingAdminPolicyButStillChecksCreator()
    {
        await using var fixture = await Fixture.CreateAsync(open: true);
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        var aliceWithoutRole = User("alice@example.com");
        Assert.True(await fixture.Publishing.CanPublishAsync(aliceWithoutRole));
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, aliceWithoutRole);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, User("bob@example.com")));
    }

    [Fact]
    public void MigrationAllowsIndependentDashboardNamesAndModelSnapshotMatches()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;").Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261019142000_NormalizeDomainControllerRole", "20261020170000_AllowSharedDashboardNames");
        Assert.Contains("DROP INDEX [IX_AnalysisDashboardTemplates_Name_Version]", script);
        Assert.Contains("CREATE INDEX [IX_AnalysisDashboardTemplates_Name_Version]", script);
        Assert.DoesNotContain("CREATE UNIQUE INDEX", script);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ReadOnlyGridPreservesLayoutAndEscapesNamesWithoutEditOrDragControls()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var document = CreateDashboard().Active.Document;
        document.Widgets[0].Title = "<LDAP latency>";
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<PublishedAnalysisDashboardGrid>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Document"] = document }))).ToHtmlString());
        Assert.Contains("Read-only published dashboard", html);
        Assert.Contains("&lt;LDAP latency&gt;", html);
        Assert.Contains("--columns:4;--rows:12;--row-height:88px", html);
        Assert.Contains("grid-column:2 / span 2;grid-row:3 / span 4", html);
        Assert.DoesNotContain("data-inbox-drag-handle", html);
        Assert.DoesNotContain("data-inbox-widget-id", html);
        Assert.DoesNotContain("data-inbox-resize-handle", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("draggable", html);
    }

    [Fact]
    public async Task OpenViewReloadsRepublishedSnapshotAndRemovesWithdrawnContentEvenWithQueryRefreshOff()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var options = new CrossTenantLogAnalyticsOptions { Enabled = false };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var clock = new AnalysisRefreshLoopTests.ManualTimeProvider();
        var components = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(options).AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();
        await using (var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()))
        {
            RenderFragment child = builder =>
            {
                builder.OpenComponent<PublishedAnalysisDashboardView>(0);
                builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                builder.CloseComponent();
            };
            var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = Task.FromResult(new AuthenticationState(Operator)),
                        ["ChildContent"] = child
                    })));
            var initial = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            Assert.Contains("AD dashboard", initial);
            Assert.Contains("DC01", initial);
            Assert.Contains("Live Log Analytics is disabled", initial);
            Assert.Equal(TimeSpan.FromSeconds(30), clock.Period);
            Assert.NotNull(components.ExecutionControls);
            await renderer.Dispatcher.InvokeAsync(() =>
                components.ExecutionControls.Changed.InvokeAsync(((int?)60, (int?)15)));
            var sessionHtml = System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
            Assert.Contains("<small>1h · 15 min · SourceDSA</small>", sessionHtml);
            var originalPublication = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
            Assert.Null(originalPublication.Document.TimeRangeMinutesOverride);
            Assert.Null(originalPublication.Document.BinMinutesOverride);
            var stored = await fixture.Repository.LoadAsync("alice@example.com");
            stored.Collection!.RenameActive("Republished dashboard");
            stored.Collection.Active.Document.TimeRangeMinutesOverride = 10080;
            stored.Collection.Active.Document.BinMinutesOverride = 30;
            await fixture.Repository.SaveAsync("alice@example.com", stored.Collection, stored.Revision);
            await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 3, Alice);
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.DoesNotContain("Republished dashboard", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForHtmlAsync("Republished dashboard");
            Assert.Contains("version 2", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
            Assert.Equal(10080, components.ExecutionControls.TimeRangeMinutes);
            Assert.Equal(30, components.ExecutionControls.BinMinutes);
            await fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice);
            clock.Advance(TimeSpan.FromSeconds(30));
            var withdrawn = await WaitForHtmlAsync("no longer published");
            Assert.DoesNotContain("DC01", withdrawn);
            Assert.DoesNotContain("LDAP latency", withdrawn);
            Assert.DoesNotContain("Read-only published dashboard", withdrawn);
            Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Widgets);

            async Task<string> WaitForHtmlAsync(string expected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
                    if (html.Contains(expected, StringComparison.Ordinal))
                        return html;
                    await Task.Delay(10, timeout.Token);
                }
            }
        }
        Assert.True(clock.Disposed);
    }

    private static AnalysisDashboardCollection CreateDashboard()
    {
        var collection = AnalysisDashboardCollection.Create();
        collection.RenameActive("AD dashboard");
        collection.HiddenQueryIds.Add(Guid.NewGuid());
        collection.Active.Document.HiddenQueryIds.Add(Guid.NewGuid());
        collection.Active.Document.Systems.Add(new() { SubscriptionId = "sub", Computer = "DC01" });
        collection.Active.Document.Widgets.Add(new() { QueryDefinitionId = Guid.NewGuid(), Title = "LDAP latency", TimeRangeMinutes = 240, Column = 2, Row = 3 });
        return collection;
    }

    private static ClaimsPrincipal User(string name, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        if (role is not null)
            claims.Add(new(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "test"));
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, TestDbContextFactory factory) : IAsyncDisposable
    {
        public IDbContextFactory<AlertDbContext> Factory => factory;
        public AnalysisDashboardRepository Repository { get; } = new(factory);
        public AnalysisDashboardPublishing Publishing { get; } = new(factory,
            services.GetRequiredService<IAuthorizationService>(), new CurrentUserIdentity(new HttpContextAccessor()),
            services.GetRequiredService<ApplicationAuthenticationOptions>());

        public static async Task<Fixture> CreateAsync(bool open = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = factory.CreateDbContext();
            var statements = context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement =>
                    statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisDashboards\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisDashboardTemplates\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisQueryDefinitions\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisQueryWorkspaces\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisQueryVersions\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"ComputerInventory\" ", StringComparison.Ordinal)
                    || statement.Contains(" ON \"AnalysisDashboardTemplates\" ", StringComparison.Ordinal));
            foreach (var statement in statements)
                await context.Database.ExecuteSqlRawAsync(statement
                    .Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            var services = new ServiceCollection().AddLogging().AddSingleton(new ApplicationAuthenticationOptions
            {
                Type = open ? ApplicationAuthenticationOptions.Open : ApplicationAuthenticationOptions.Sql
            }).AddAuthorization(options =>
            {
                options.AddPolicy("AdminAccess", policy => policy.RequireAssertion(context => open || context.User.IsInRole(SqlAuthenticationRoles.Admin)));
                options.AddPolicy("OperatorAccess", policy => policy.RequireAssertion(context => open
                    || context.User.IsInRole(SqlAuthenticationRoles.Operator) || context.User.IsInRole(SqlAuthenticationRoles.Admin)));
            }).BuildServiceProvider();
            return new(connection, services, factory);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class CapturingComponentActivator : IComponentActivator
    {
        public AnalysisDashboardExecutionControls? ExecutionControls { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AnalysisDashboardExecutionControls controls)
                ExecutionControls = controls;
            return component;
        }
    }

    private sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode
                ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Render mode {renderMode.GetType().Name} is not supported in static page tests.");
    }

    private sealed class UnusedJavaScript : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static HTML rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static HTML rendering must not invoke JavaScript.");
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Read-only rendering must not access Key Vault.");
    }
}
