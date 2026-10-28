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
    public async Task OperatorCanKeepAnIndependentPrivateCopyOfAnotherUsersPublishedDashboard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", original, 0);
        var publication = await fixture.Publishing.PublishAsync(original.ActiveDashboardId, 1, Alice);
        var snapshot = await fixture.Publishing.LoadAsync(publication.Id, Operator);
        var personal = AnalysisDashboardCollection.Create();
        personal.AddCopy(snapshot.Document);
        await fixture.Repository.SaveAsync("operator@example.com", personal, 0);
        var restored = (await fixture.Repository.LoadAsync("operator@example.com")).Collection!;
        Assert.NotEqual(publication.Id, restored.ActiveDashboardId);
        Assert.Equal("AD dashboard (copy)", restored.Active.Document.Name);
        restored.Active.Document.Widgets[0].Title = "Operators private customization";
        await fixture.Repository.SaveAsync("operator@example.com", restored, 1);
        await fixture.Publishing.WithdrawAsync(publication.Id, Alice);
        Assert.Equal("Operators private customization",
            (await fixture.Repository.LoadAsync("operator@example.com")).Collection!.Active.Document.Widgets[0].Title);
        Assert.Equal("LDAP latency",
            (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Widgets[0].Title);
        Assert.Empty(await fixture.Publishing.ListAsync(Operator));
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

    [Theory]
    [InlineData(false, null, null, null, null, "")]
    [InlineData(true, null, null, null, null, "second-configured")]
    [InlineData(true, "first-configured", " ZULU.EXAMPLE. ", " North ", " Domain Controller ", "first-configured")]
    [InlineData(true, "", "", "", "", "")]
    public async Task AnalysisPageDefaultsToCompactViewWithExistingDashboardsSystemsAndWidgets(
        bool configuredWorkspaces, string? defaultWorkspace, string? defaultDomain, string? defaultSite, string? defaultRole, string expectedWorkspace)
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            (await context.AnalysisDashboardTemplates.SingleAsync()).Version = 7;
            context.ComputerInventory.AddRange(
                new() { SubscriptionId = "sub", Computer = "DC01", Domain = "zulu.example", Site = "North", Role = "Domain Controller" },
                new() { SubscriptionId = "other-sub", Computer = "DC02", Domain = "alpha.example", Site = "South", Role = "SQL" });
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
        var options = new CrossTenantLogAnalyticsOptions
        {
            Workspaces = configuredWorkspaces
                ? [
                    new() { Key = "first-configured", DisplayName = "Zulu", Domains = ["zulu.example"] },
                    new() { Key = "second-configured", DisplayName = "alpha", Domains = ["alpha.example"] }
                ]
                : []
        };
        var components = new CapturingComponentActivator();
        var defaults = new DashboardFilterOptions
        {
            Workspace = defaultWorkspace, Domain = defaultDomain, Site = defaultSite, Role = defaultRole
        };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var configuration = new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder());
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(components)
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
            .AddSingleton(defaults)
            .AddSingleton(options)
            .AddSingleton(TimeProvider.System)
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
        Assert.Contains($"{publication.Name} · v7", html);
        Assert.DoesNotContain($"{publication.Name} · {publication.Owner}", html);
        Assert.Contains("aria-label=\"System selection and dashboard filters\"", html);
        foreach (var label in new[] { "Workspace", "Domain", "Site", "Role" })
            Assert.Equal(1, html.Split($"<span>{label}</span>", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Applies to system-based widgets", html);
        Assert.Contains("<span>Edit</span>", html);
        Assert.Contains("Select dashboard", html);
        Assert.Contains("Duplicate dashboard", html);
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
        var page = Assert.IsType<MonitoringApp.Components.Pages.Analysis>(components.AnalysisPage);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var workspaceField = page.GetType().GetField("workspaceFilter", flags)!;
        Assert.Equal(expectedWorkspace, workspaceField.GetValue(page));
        Assert.Equal(CrossTenantLogAnalyticsOptions.NormalizeDomain(defaultDomain), page.GetType().GetField("domainFilter", flags)!.GetValue(page));
        Assert.Equal(defaultSite?.Trim() ?? "", page.GetType().GetField("siteFilter", flags)!.GetValue(page));
        Assert.Equal(defaultRole?.Trim() ?? "", page.GetType().GetField("roleFilter", flags)!.GetValue(page));
        page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, publication.Id);
        await renderer.Dispatcher.InvokeAsync(() =>
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
        var filteredSystems = Assert.IsAssignableFrom<IReadOnlyList<ComputerInventoryEntry>>(components.PublishedView!.FilteredSystems);
        Assert.Equal(configuredWorkspaces && defaultWorkspace != "" ? 1 : 2, filteredSystems.Count);
        if (configuredWorkspaces && defaultWorkspace != "")
            Assert.Equal(defaultWorkspace is null ? "DC02" : "DC01", Assert.Single(filteredSystems).Computer);
        workspaceField.SetValue(page, "");
        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)page.GetType().GetMethod("WorkspaceChangedAsync", flags)!.Invoke(page, [])!);
        Assert.Equal("", workspaceField.GetValue(page));

        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
        Assert.Null(page.GetType().GetField("publicationError", flags)!.GetValue(page));
        Assert.Null(page.GetType().GetField("selectedPublishedDashboard", flags)!.GetValue(page));
        Assert.True((bool)page.GetType().GetField("editMode", flags)!.GetValue(page)!);
        var copied = (await fixture.Repository.LoadAsync("alice@example.com")).Collection!;
        Assert.Equal(2, copied.Dashboards.Count);
        Assert.NotEqual(publication.Id, copied.ActiveDashboardId);
        Assert.Equal("AD dashboard (copy)", copied.Active.Document.Name);
        Assert.Equal("DC01", Assert.Single(copied.Active.Document.Systems).Computer);
        Assert.Equal("LDAP latency", Assert.Single(copied.Active.Document.Widgets).Title);
        Assert.Null(page.GetType().GetProperty("OwnPublication", flags)!.GetValue(page));
        Assert.Single(await fixture.Publishing.ListAsync(Alice));
        var source = await fixture.Publishing.LoadAsync(publication.Id, Alice);
        Assert.Equal("AD dashboard", source.Document.Name);
        Assert.Equal(7, source.Publication.Version);
        var state = (AnalysisDashboardState)page.GetType().GetProperty("Dashboard", flags)!.GetValue(page)!;
        state.UpdateWorkspace(document =>
        {
            document.Name = "Edited personal copy";
            document.Widgets[0].Title = "Private widget edit";
        });
        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
        var personalCopies = (await fixture.Repository.LoadAsync("alice@example.com")).Collection!;
        Assert.Equal(3, personalCopies.Dashboards.Count);
        Assert.Equal("Edited personal copy (copy)", personalCopies.Active.Document.Name);
        Assert.Equal("Private widget edit", personalCopies.Active.Document.Widgets[0].Title);
        Assert.Equal("Edited personal copy", personalCopies.Dashboards.Single(entry => entry.Id == copied.ActiveDashboardId).Document.Name);
        Assert.Equal("LDAP latency", (await fixture.Publishing.LoadAsync(publication.Id, Alice)).Document.Widgets[0].Title);
        Assert.Single(await fixture.Publishing.ListAsync(Alice));

        if (!configuredWorkspaces)
        {
            var current = await fixture.Repository.LoadAsync("alice@example.com");
            await fixture.Repository.SaveAsync("alice@example.com", current.Collection!, current.Revision);
            page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, publication.Id);
            await renderer.Dispatcher.InvokeAsync(async () =>
                await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
            Assert.Contains("another session", Assert.IsType<string>(
                page.GetType().GetField("publicationError", flags)!.GetValue(page)));
            Assert.Equal(publication.Id, page.GetType().GetField("selectedPublishedDashboard", flags)!.GetValue(page));
            Assert.Equal(personalCopies.ActiveDashboardId,
                ((AnalysisDashboardCollection)page.GetType().GetField("dashboardCollection", flags)!.GetValue(page)!).ActiveDashboardId);
            Assert.Equal(3, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
            await fixture.Publishing.WithdrawAsync(publication.Id, Alice);
            page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, publication.Id);
            await renderer.Dispatcher.InvokeAsync(async () =>
                await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
            Assert.Contains("no longer published", Assert.IsType<string>(
                page.GetType().GetField("publicationError", flags)!.GetValue(page)));
            Assert.Equal(3, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
        }
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
    public async Task PublishedSystemListTracksFilteredInventoryWithoutChangingThePublishedSelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        collection.Active.Document.Systems.AddRange(
        [
            new() { SubscriptionId = "sub", Computer = "DC02" },
            new() { SubscriptionId = "other-sub", Computer = "DC01" },
            new() { SubscriptionId = "sub", Computer = "Missing" }
        ]);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var options = new CrossTenantLogAnalyticsOptions();
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var components = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(options).AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
            await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Operator)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent<PublishedAnalysisDashboardView>(0);
                        builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                        builder.AddAttribute(2, "ShowControls", false);
                        builder.AddAttribute(3, "FilteredSystems", new ComputerInventoryEntry[]
                        {
                            new() { SubscriptionId = "SUB", Computer = "dc01" },
                            new() { SubscriptionId = "sub", Computer = "Unselected" }
                        });
                        builder.CloseComponent();
                    })
                })));
        var html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Selected systems: DC01</p>", html);
        Assert.DoesNotContain("DC02", html);
        Assert.DoesNotContain("Unselected", html);
        Assert.DoesNotContain("Missing", html);

        await ApplyFilterAsync([new() { SubscriptionId = "sub", Computer = "DC02" }]);
        html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Selected systems: DC02</p>", html);
        Assert.DoesNotContain("DC01", html);
        await ApplyFilterAsync([]);
        Assert.Contains("Selected systems: None</p>", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        await ApplyFilterAsync(
        [
            new() { SubscriptionId = "sub", Computer = "DC01" },
            new() { SubscriptionId = "sub", Computer = "DC02" },
            new() { SubscriptionId = "other-sub", Computer = "DC01" }
        ]);
        Assert.Contains("Selected systems: DC01, DC02, DC01</p>", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Equal(4, (await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator)).Document.Systems.Count);
        Assert.Equal(4, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Systems.Count);

        Task ApplyFilterAsync(ComputerInventoryEntry[] systems) => renderer.Dispatcher.InvokeAsync(() =>
            components.PublishedView!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["FilteredSystems"] = systems
            })));
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task RefreshesRenderEachWidgetBeforeStartingTheNextAndRespectWithdrawal(bool published, bool withdraw, bool timeout)
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        var lower = collection.Active.Document.Widgets[0];
        lower.Row = 7;
        var upper = new AnalysisDashboardWidget
        {
            QueryDefinitionId = Guid.NewGuid(), Title = "Upper widget", Row = 1, Column = 1,
            TimeRangeMinutes = 240
        };
        collection.Active.Document.Widgets.Add(upper);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            foreach (var widget in collection.Active.Document.Widgets)
            {
                var query = ActiveDirectoryAnalysisQueries.Create().First();
                query.Id = widget.QueryDefinitionId;
                query.Key = widget.QueryDefinitionId.ToString("N");
                var version = new AnalysisQueryVersion
                {
                    Id = Guid.NewGuid(), Version = 1,
                    SnapshotJson = System.Text.Json.JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
                };
                query.Versions.Add(version);
                query.PublishedVersionId = version.Id;
                context.AnalysisQueryDefinitions.Add(query);
            }
            await context.SaveChangesAsync();
        }
        var options = new CrossTenantLogAnalyticsOptions { Enabled = true };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var gated = new GatedDbContextFactory(fixture.Factory, timeout);
        var components = new CapturingComponentActivator();
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var configuration = new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder());
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(gated, runner))
            .AddSingleton(runner)
            .AddSingleton(new AlertStore(fixture.Factory, configuration, presenter, new AlertRuleEvaluator(presenter),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton(new AnalysisDashboardState())
            .AddSingleton(fixture.Repository)
            .AddSingleton(new DashboardFilterOptions())
            .AddSingleton(options)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new UnusedJavaScript())
            .BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent(0, published ? typeof(PublishedAnalysisDashboardView) : typeof(MonitoringApp.Components.Pages.Analysis));
                        if (published) builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                        builder.CloseComponent();
                    })
                }));
            await root.QuiescenceTask;
            return root;
        });
        var component = published ? (IComponent)components.PublishedView! : components.AnalysisPage!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var results = (Dictionary<string, AnalysisQueryResponse>)component.GetType()
            .GetField(published ? "results" : "widgetResults", flags)!.GetValue(component)!;
        results[upper.Id] = CachedResult("OLD_UPPER");
        results[lower.Id] = CachedResult("OLD_LOWER");
        Task refresh = Task.CompletedTask;
        try
        {
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                refresh = (Task)component.GetType().GetMethod(published ? "RunAsync" : "RunDashboardAsync", flags)!
                    .Invoke(component, [])!;
            });
            await gated.Started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gated.Started[1].Task.IsCompleted);
            var before = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            Assert.Contains("OLD_UPPER", before);
            Assert.Contains("OLD_LOWER", before);

            if (withdraw)
            {
                await fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice);
                gated.Release[0].TrySetResult();
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(gated.Started[1].Task.IsCompleted);
                var withdrawn = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
                Assert.DoesNotContain("OLD_UPPER", withdrawn);
                Assert.DoesNotContain("OLD_LOWER", withdrawn);
                Assert.Contains("no longer published", withdrawn);
                return;
            }

            gated.Release[0].TrySetResult();
            await gated.Started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            var during = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            Assert.DoesNotContain("OLD_UPPER", during);
            Assert.Contains("OLD_LOWER", during);
            var errors = (Dictionary<string, string>)component.GetType()
                .GetField(published ? "errors" : "widgetErrors", flags)!.GetValue(component)!;
            Assert.True(errors.ContainsKey(upper.Id));
            if (timeout)
                Assert.Contains("timed out", errors[upper.Id]);
            Assert.False(errors.ContainsKey(lower.Id));
            Assert.Contains(errors[upper.Id], during);
            Assert.Equal(2, gated.RequestCount);

            gated.Release[1].TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, errors.Count);
            Assert.False((bool)component.GetType().GetField(published ? "running" : "isDashboardRunning", flags)!.GetValue(component)!);
        }
        finally
        {
            foreach (var release in gated.Release) release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }

        static AnalysisQueryResponse CachedResult(string marker)
        {
            var now = DateTimeOffset.UtcNow;
            return new(Guid.NewGuid(), marker, 1, "ms", now.AddHours(-1), now, now, 0, [], [],
                AnalysisQueryVisualizationTypes.Table,
                [new("Cached workspace", [new("Value", "string")], [[marker]])]);
        }
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
        public MonitoringApp.Components.Pages.Analysis? AnalysisPage { get; private set; }
        public PublishedAnalysisDashboardView? PublishedView { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AnalysisDashboardExecutionControls controls)
                ExecutionControls = controls;
            if (component is MonitoringApp.Components.Pages.Analysis page)
                AnalysisPage = page;
            if (component is PublishedAnalysisDashboardView view)
                PublishedView = view;
            return component;
        }
    }

    private sealed class GatedDbContextFactory(IDbContextFactory<AlertDbContext> inner, bool timeoutFirst = false) : IDbContextFactory<AlertDbContext>
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public TaskCompletionSource[] Started { get; } =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public TaskCompletionSource[] Release { get; } =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];

        public AlertDbContext CreateDbContext() => inner.CreateDbContext();

        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref requestCount) - 1;
            Started[index].TrySetResult();
            await Release[index].Task.WaitAsync(cancellationToken);
            if (index == 0 && timeoutFirst)
                throw new TaskCanceledException("The first widget timed out.");
            return await inner.CreateDbContextAsync(cancellationToken);
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
