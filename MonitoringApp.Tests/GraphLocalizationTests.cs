using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class GraphLocalizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Target = "DC<01>";
    private const string Subscription = "sub<&>";

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    public async Task SLocationsGatesCalendarAndDevelopmentPreviewWithoutRemovingNormalMap(string culture, bool enabled)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create("Development", sLocations: enabled);
        await fixture.Render<Graph>();
        await fixture.Set("currentTimeUtc", new DateTimeOffset(2026, 1, 29, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(enabled, fixture.Html.Contains("graph-easter-egg-duke", StringComparison.Ordinal));
        Assert.Equal(enabled, fixture.Html.Contains("graph-easter-preview", StringComparison.Ordinal));
        Assert.NotEmpty(SiteMarkers(fixture.Html));
        fixture.Page.EasterEggPreview = "duke-nukem-day";
        await fixture.Invoke("OnParametersSet");
        Assert.Equal(enabled, fixture.Html.Contains("graph-easter-egg-duke", StringComparison.Ordinal));
        await fixture.Set("currentTimeUtc", Now);
        Assert.Equal(enabled, fixture.Html.Contains("graph-easter-egg-duke", StringComparison.Ordinal));
        fixture.Page.EasterEggPreview = "unknown-preview";
        await fixture.Invoke("OnParametersSet");
        Assert.Equal(enabled, fixture.Decoded.Contains(fixture.Text["Graph.PreviewUnknown"], StringComparison.Ordinal));
        if (!enabled) Assert.Empty(fixture.Logger.Entries);
    }

    [Theory]
    [InlineData("en-US", 1)]
    [InlineData("en-US", 7)]
    [InlineData("de-DE", 1)]
    [InlineData("de-DE", 7)]
    public async Task EmptyGraphTranslatesWholeDescriptionsAndPreservesConfiguredChoices(string culture, int days)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(days: days);
        var original = JsonSerializer.Serialize(fixture.Options);
        await fixture.Render<Graph>();
        foreach (var key in new[] { "Graph.Title", "Graph.Empty", "Graph.EmptyHelp", "Graph.FilterHelp",
            "Graph.SiteStatusHelp", "Graph.ServiceHealth", "Graph.Maintenance" })
            Assert.Contains(fixture.Text[key], fixture.Decoded);
        Assert.Contains(fixture.Text.Plural("Graph.Description", days), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Graph.CanvasDescription", 0, 0), fixture.Decoded);
        Assert.Contains("value=\"Subscription\"", fixture.Html);
        Assert.Contains("value=\"ResourceGroup\"", fixture.Html);
        Assert.Contains("value=\"Target\"", fixture.Html);
        Assert.Contains("Original &lt;subscription&gt;", fixture.Html);
        Assert.Contains("World map configurable.svg", fixture.Html);
        Assert.Contains("https://commons.wikimedia.org/wiki/File:World_map_configurable.svg", fixture.Html);
        Assert.Contains("Wikimedia Commons", fixture.Html);
        Assert.Contains("CC0", fixture.Html);
        Assert.DoesNotContain("graph-easter-preview", fixture.Html);
        Assert.Equal(original, JsonSerializer.Serialize(fixture.Options));
    }

    [Theory]
    [InlineData("en-US", AlertGraphTargetStatus.OpenAlert, "graph-svg-active")]
    [InlineData("de-DE", AlertGraphTargetStatus.OpenAlert, "graph-svg-active")]
    [InlineData("en-US", AlertGraphTargetStatus.ServiceHealthAlert, "graph-svg-target-service-health")]
    [InlineData("de-DE", AlertGraphTargetStatus.ServiceHealthAlert, "graph-svg-target-service-health")]
    [InlineData("en-US", AlertGraphTargetStatus.Current, "graph-svg-target-current")]
    [InlineData("de-DE", AlertGraphTargetStatus.Current, "graph-svg-target-current")]
    [InlineData("en-US", AlertGraphTargetStatus.Stale, "graph-svg-target-stale")]
    [InlineData("de-DE", AlertGraphTargetStatus.Stale, "graph-svg-target-stale")]
    [InlineData("en-US", AlertGraphTargetStatus.Maintenance, "graph-svg-target-maintenance")]
    [InlineData("de-DE", AlertGraphTargetStatus.Maintenance, "graph-svg-target-maintenance")]
    public async Task TargetStatusWholeAriaAndHeartbeatTitleKeepCountsIdentityAndCss(
        string culture, AlertGraphTargetStatus status, string css)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        var record = Record() with
        {
            IsInventoryOnly = status != AlertGraphTargetStatus.OpenAlert,
            AlertId = status == AlertGraphTargetStatus.OpenAlert ? "Original.Alert" : "",
            MonitorCondition = status == AlertGraphTargetStatus.OpenAlert ? "Fired" : "",
            LastHeartbeatAt = status == AlertGraphTargetStatus.Stale ? null : Now.AddMinutes(-10),
            Maintenance = status == AlertGraphTargetStatus.Maintenance,
            HasServiceHealthAlert = status == AlertGraphTargetStatus.ServiceHealthAlert
        };
        await fixture.Show([record]);
        var original = JsonSerializer.Serialize(record);
        var layout = AlertGraphLayout.Build(AlertGraphHierarchy.Build([record],
            AlertGraphLayer.Subscription, AlertGraphLayer.ResourceGroup, AlertGraphLayer.Target));
        var target = Assert.Single(layout.Nodes, node => node.Layer == AlertGraphLayer.Target);
        Assert.Equal(status, AlertGraphTargetStatusResolver.Resolve(target, Now, TimeSpan.FromMinutes(10)));
        var expectedAria = fixture.Text.Format("Graph.NodeAriaTarget." + status,
            3, Target, target.Count, target.HistoryCount, Subscription);
        Assert.Contains($"aria-label=\"{expectedAria}\"", fixture.Decoded);
        Assert.Contains(css, fixture.Html);
        var titleKey = record.LastHeartbeatAt is null ? "Graph.NodeTitleTarget" : "Graph.NodeTitleTargetHeartbeat";
        var expectedTitle = fixture.Text.Format(titleKey, 3, Target, target.Count, target.HistoryCount,
            Subscription, record.LastHeartbeatAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
        Assert.Contains($"<title>{expectedTitle}</title>", fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Graph.TargetCurrent", 10), fixture.Decoded);
        Assert.Contains("DC&lt;01&gt;", fixture.Html);
        Assert.Contains("sub&lt;&amp;&gt;", fixture.Html);
        Assert.DoesNotContain("<script", fixture.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, JsonSerializer.Serialize(record));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task FilteringOnlyChangesHierarchyAndClearKeepsOriginalDataAndGlobalMap(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        await fixture.Show([Record()]);
        var before = SiteMarkers(fixture.Html);
        Assert.NotEmpty(before);
        await fixture.Set("filterText", "No-Matching-Scope");
        await fixture.Invoke("GraphFilterChanged");
        Assert.Contains(fixture.Text["Graph.EmptyFiltered"], fixture.Decoded);
        Assert.Contains(fixture.Text["Graph.EmptyFilteredHelp"], fixture.Decoded);
        Assert.Contains(fixture.Text["Graph.ClearFilterLabel"], fixture.Decoded);
        Assert.Equal(before, SiteMarkers(fixture.Html));
        await fixture.Invoke("ClearFilter");
        Assert.DoesNotContain(fixture.Text["Graph.EmptyFiltered"], fixture.Decoded);
        Assert.Equal(before, SiteMarkers(fixture.Html));
        Assert.Equal("", fixture.Get<string>("filterText"));
        await fixture.Set("filterText", "ORIGINAL.SITE");
        Assert.Contains("DC&lt;01&gt;", fixture.Html);
    }

    [Fact]
    public async Task CultureSwitchKeepsSvgGeometryDnsGroupingOriginalLabelsAndJavascriptContracts()
    {
        using var selection = new UiSelection("en-US");
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        var record = Record();
        await fixture.Show([record]);
        await fixture.Set("selectedLayer1", AlertGraphLayer.Domain);
        var before = Geometry(fixture.Html);
        var original = JsonSerializer.Serialize(fixture.Options);
        Assert.Contains(fixture.Text["Graph.DnsSegmentsLabel"], fixture.Decoded);
        using (new UiSelection("de-DE"))
        {
            await fixture.Invoke("GraphLayersChanged");
            Assert.Contains(fixture.Text["Graph.DnsSegmentsLabel"], fixture.Decoded);
            Assert.Equal(before, Geometry(fixture.Html));
            Assert.Contains("child.example.org", fixture.Decoded);
            Assert.Contains(fixture.Text.Format("Graph.Updated", "2026-03-20 12:00:00"), fixture.Decoded);
            await fixture.Invoke("OnAfterRenderAsync", true);
            await fixture.Invoke("ZoomAsync", 1.25);
            await fixture.Invoke("ZoomAsync", 0.8);
            await fixture.Invoke("ResetGraphAsync");
            Assert.Collection(fixture.Javascript.Calls,
                call => Assert.Equal(("alertGraph.initialize", "alert-graph-canvas"), (call.Name, call.Arguments[0])),
                call => { Assert.Equal("alertGraph.zoom", call.Name); Assert.Equal(1.25, call.Arguments[1]); },
                call => { Assert.Equal("alertGraph.zoom", call.Name); Assert.Equal(0.8, call.Arguments[1]); },
                call => Assert.Equal(("alertGraph.reset", "alert-graph-canvas"), (call.Name, call.Arguments[0])));
        }
        Assert.Equal(original, JsonSerializer.Serialize(fixture.Options));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("en-US", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 0)]
    [InlineData("de-DE", 1)]
    [InlineData("de-DE", 2)]
    public async Task MapProjectsTypedCountsAndAllSolarPhasesWithoutChangingNeutralMarkers(string culture, int count)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var locations = new[] { 0d, 94d, 180d }.Select((longitude, index) =>
            new AlertGraphSiteLocationOptions
            {
                DisplayName = $"Original <site {index}>", Latitude = 0, Longitude = longitude,
                Aliases = [$"alias-{index}"]
            }).ToArray();
        var records = locations.SelectMany((location, index) => Enumerable.Range(0, count)
            .Select(number => Record() with { Id = Guid.NewGuid(), AlertId = $"alert-{index}-{number}", Site = location.Aliases[0] })).ToArray();
        var markers = AlertGraphSiteMap.Build(records, locations, Now);
        Assert.Equal(new[] { AlertGraphSolarPhase.Day, AlertGraphSolarPhase.Twilight, AlertGraphSolarPhase.Night },
            markers.Select(marker => marker.SolarPhase));
        var original = JsonSerializer.Serialize(markers);
        await fixture.Render<GraphSiteMap>(MapParameters(records, locations, null));
        foreach (var marker in markers)
        {
            var phase = fixture.Text[marker.SolarPhase switch
            {
                AlertGraphSolarPhase.Day => "GraphMap.Daylight",
                AlertGraphSolarPhase.Twilight => "Graph.Twilight",
                _ => "Graph.Night"
            }];
            var suffix = count == 0 ? "NoOpen" : count == 1 ? "One" : "Other";
            Assert.Contains(fixture.Text.Format("GraphMap.SiteDescription." + suffix, marker.Name, count, phase), fixture.Decoded);
            Assert.Contains(fixture.Text.Format("GraphMap.SiteTitle." + suffix, marker.Name, count, phase), fixture.Decoded);
            Assert.Contains(count == 0 ? "No open alerts" : $"{count} open alert", marker.StatusLabel);
        }
        Assert.Contains("Original &lt;site 0&gt;", fixture.Html);
        Assert.Equal(original, JsonSerializer.Serialize(AlertGraphSiteMap.Build(records, locations, Now)));
    }

    [Theory]
    [InlineData("en-US", "duke-nukem-day")]
    [InlineData("de-DE", "duke-nukem-day")]
    [InlineData("en-US", "moon-landing")]
    [InlineData("de-DE", "moon-landing")]
    [InlineData("en-US", "warhammer-day")]
    [InlineData("de-DE", "warhammer-day")]
    [InlineData("en-US", "april-fools")]
    [InlineData("de-DE", "april-fools")]
    public async Task DecorativeMapFramesTranslateButCalendarNamesThemesPositionsAndAttributionsStayOriginal(
        string culture, string key)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var egg = AlertGraphEasterEggSites.ResolvePreview(key, true)!;
        var original = JsonSerializer.Serialize(egg);
        await fixture.Render<GraphSiteMap>(MapParameters([], [], egg));
        Assert.Contains(egg.Heading, fixture.Decoded);
        Assert.Contains($"graph-easter-egg-{egg.Theme}", fixture.Html);
        foreach (var site in egg.Sites)
        {
            Assert.Contains(site.Name, fixture.Decoded);
            if (site.IsOrbiting)
                Assert.Contains(fixture.Text.Format("GraphMap.EarthOrbit", site.Name), fixture.Decoded);
        }
        var moonCount = egg.Sites.Count(site => site.MoonPosition is not null);
        if (moonCount > 0)
        {
            Assert.Contains(fixture.Text.Plural("GraphMap.MoonDescription", moonCount), fixture.Decoded);
            Assert.Contains("href=\"/moon-easter-egg.png\"", fixture.Html);
            var heading = fixture.Text[egg.Theme == "lunar" ? "GraphMap.MoonAnniversary" : "GraphMap.Moon"];
            Assert.Contains($"<h3 id=\"graph-moon-heading\">{heading}</h3>", fixture.Decoded);
        }
        if (key == "warhammer-day")
        {
            Assert.Contains(fixture.Text["GraphMap.MarsDescription"], fixture.Decoded);
            Assert.Contains("Property of Fabricator-General of Mars, Adeptus Mechanicus", fixture.Decoded);
            Assert.Contains("translate(177.02 231.876)", fixture.Html);
        }
        if (key == "april-fools")
            Assert.DoesNotContain("aria-label=\"Atlantis", fixture.Html);
        Assert.Equal(original, JsonSerializer.Serialize(egg));
    }

    [Theory]
    [InlineData("en-US", "Development")]
    [InlineData("de-DE", "Development")]
    [InlineData("en-US", "Production")]
    [InlineData("de-DE", "Production")]
    public async Task PreviewKeysDevelopmentGateAndLocalizedValidationKeepNeutralServiceContracts(string culture, string environment)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(environment);
        await fixture.Render<Graph>();
        fixture.Page.EasterEggPreview = "duke-nukem-day";
        await fixture.Invoke("OnParametersSet");
        if (environment == "Development")
        {
            Assert.Contains(fixture.Text["Graph.PreviewLabel"], fixture.Decoded);
            Assert.Contains("/graph?easterEgg=duke-nukem-day", fixture.Html);
            Assert.Contains("graph-moon-panel", fixture.Html);
        }
        else
        {
            Assert.DoesNotContain("graph-easter-preview", fixture.Html);
            Assert.DoesNotContain("graph-moon-panel", fixture.Html);
        }
        fixture.Page.EasterEggPreview = "SECRET <invalid-preview>";
        await fixture.Invoke("OnParametersSet");
        if (environment == "Development")
        {
            Assert.Contains(fixture.Text["Graph.PreviewUnknown"], fixture.Decoded);
            Assert.DoesNotContain("SECRET", fixture.Decoded);
            Assert.Null(fixture.Get<AlertGraphEasterEgg?>("previewEasterEgg"));
            foreach (var failure in new Exception[]
            {
                new ArgumentException("Unknown Easter-egg preview. Choose one of the preview links."),
                new System.Text.Json.JsonException("SECRET <parser>"),
                new InvalidOperationException("Wrapper", new AlertGraphPreviewException("SECRET <preview>"))
            })
            {
                await fixture.Invoke("ReportPreviewError", failure);
                Assert.Contains(fixture.Text["Graph.PreviewFailed"], fixture.Decoded);
                Assert.DoesNotContain("SECRET", fixture.Decoded);
            }
            fixture.Page.EasterEggPreview = "duke-nukem-day";
            await fixture.Invoke("OnParametersSet");
            Assert.DoesNotContain(fixture.Text["Graph.PreviewUnknown"], fixture.Decoded);
            Assert.DoesNotContain(fixture.Text["Graph.PreviewFailed"], fixture.Decoded);
            Assert.Contains("graph-moon-panel", fixture.Html);
        }
        else
        {
            Assert.DoesNotContain("Unknown Easter-egg preview", fixture.Decoded);
            Assert.DoesNotContain(fixture.Text["Graph.PreviewUnknown"], fixture.Decoded);
            Assert.Null(fixture.Get<string?>("previewError"));
        }
    }

    private static AlertGraphRecord Record() => new(Guid.NewGuid(), Now, "Original.Alert", "Fired", "",
        "Original alert", Subscription, "Original.ResourceGroup", Target, "Original.Site", "child.example.org", "Original.Role");
    private static ParameterView MapParameters(IReadOnlyList<AlertGraphRecord> records,
        IReadOnlyList<AlertGraphSiteLocationOptions> locations, AlertGraphEasterEgg? egg) =>
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(GraphSiteMap.Records)] = records, [nameof(GraphSiteMap.Locations)] = locations,
            [nameof(GraphSiteMap.TimeUtc)] = Now, [nameof(GraphSiteMap.EasterEgg)] = egg
        });
    private static string[] Geometry(string html) => Regex.Matches(html,
        @"\b(?:viewBox|transform|x|y|x1|x2|y1|y2|cx|cy|rx|ry|r|width|height|d|points|class|href)=""[^""]*""")
        .Select(match => match.Value).ToArray();
    private static string[] SiteMarkers(string html) => Regex.Matches(html,
        @"<g class=""graph-site-marker [\s\S]*?</g>").Select(match => match.Value).ToArray();
    private static string GraphCanvas(string html) =>
        Regex.Match(html, @"<svg id=""alert-graph-canvas""[\s\S]*?</svg>").Value;

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiSelection(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/graph");
    }
    private sealed class Javascript : IJSRuntime
    {
        public List<(string Name, object?[] Arguments)> Calls { get; } = [];
        public Exception? Failure { get; set; }
        public bool BlockNextCall { get; set; }
        public TaskCompletionSource CallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            if (BlockNextCall)
            {
                BlockNextCall = false;
                CallStarted.SetResult();
                await ReleaseCall.Task.WaitAsync(token);
            }
            if (Failure is { } failure) throw failure;
            return default!;
        }
    }

    private sealed class RecordingLogger : ILogger<Graph>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }
    private sealed class Activator(IServiceProvider services) : IComponentActivator
    {
        public Graph Page { get; private set; } = default!;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is Graph page) Page = page;
            return component;
        }
    }
    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type type, int? parentId, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type)
                : throw new NotSupportedException($"Unsupported test render mode: {mode.GetType().Name}");
    }
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task GraphRefreshUpdatesInventoryHeartbeatAndRecoversAfterReadFailure(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "DC01", LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1) });
            await context.SaveChangesAsync();
        }
        await fixture.Render<Graph>();
        var cached = fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts");
        var cachedTime = fixture.Get<DateTimeOffset>("currentTimeUtc");
        var cachedAge = fixture.Get<TimeSpan>("currentHeartbeatAge");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await context.ComputerInventory.ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.LastHeartbeatAt, DateTimeOffset.UtcNow));
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"ComputerInventory\" RENAME TO \"UnavailableInventory\";");
        }
        try
        {
            await fixture.Invoke("ReloadAlertsAsync");
            Assert.Same(cached, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
            Assert.Equal(cachedTime, fixture.Get<DateTimeOffset>("currentTimeUtc"));
            Assert.Equal(cachedAge, fixture.Get<TimeSpan>("currentHeartbeatAge"));
            Assert.Contains(fixture.Text["Graph.LoadFailed"], fixture.Decoded);
        }
        finally
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableInventory\" RENAME TO \"ComputerInventory\";");
        }
        await fixture.Invoke("ReloadAlertsAsync");
        Assert.DoesNotContain(fixture.Text["Graph.LoadFailed"], fixture.Decoded);
        var target = Assert.Single(fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"), item => item.LastHeartbeatAt is not null);
        Assert.True(target.LastHeartbeatAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task GraphBrowserFailuresKeepDataLogOriginalAndRecoverWithoutImmediateRetryLoop(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        var records = new[] { Record() };
        await fixture.Show(records);
        var original = JsonSerializer.Serialize(records);
        var canvas = GraphCanvas(fixture.Html);
        var failures = new Exception[]
        {
            new JSException("PRIVATE_BROWSER_DETAIL"),
            new JSDisconnectedException("PRIVATE_BROWSER_DETAIL"),
            new TaskCanceledException("PRIVATE_BROWSER_DETAIL"),
            new InvalidOperationException("PRIVATE_BROWSER_DETAIL")
        };
        foreach (var operation in new[] { "initialize", "zoom", "reset" })
        {
            foreach (var failure in failures)
            {
                await fixture.Invoke("RetryGraphAsync");
                await fixture.Invoke("OnAfterRenderAsync", false);
                Assert.True(fixture.Get<bool>("graphInitialized"));
                if (operation == "initialize") await fixture.Invoke("GraphLayersChanged");
                fixture.Javascript.Failure = failure;
                if (operation == "initialize") await fixture.Invoke("OnAfterRenderAsync", false);
                else if (operation == "zoom") await fixture.Invoke("ZoomAsync", 1.25);
                else await fixture.Invoke("ResetGraphAsync");
                Assert.Contains(fixture.Text[operation == "initialize"
                    ? "Graph.Controls.InitializeFailed" : "Graph.Controls.ChangeFailed"], fixture.Decoded);
                Assert.Contains(fixture.Text["Graph.Controls.Retry"], fixture.Decoded);
                Assert.DoesNotContain("PRIVATE_BROWSER_DETAIL", fixture.Html);
                Assert.Same(records, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
                Assert.Equal(original, JsonSerializer.Serialize(records));
                Assert.Equal(canvas, GraphCanvas(fixture.Html));
                Assert.Contains(fixture.Logger.Entries, entry =>
                    entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
                var calls = fixture.Javascript.Calls.Count;
                await fixture.Invoke("OnAfterRenderAsync", false);
                Assert.Equal(calls, fixture.Javascript.Calls.Count);
                fixture.Javascript.Failure = null;
                await fixture.Invoke("RetryGraphAsync");
                await fixture.Invoke("OnAfterRenderAsync", false);
                Assert.True(fixture.Get<bool>("graphInitialized"));
                Assert.Null(fixture.Get<string?>("graphError"));
            }
        }
        Assert.All(fixture.Javascript.Calls, call => Assert.Equal("alert-graph-canvas", call.Arguments[0]));
        Assert.All(fixture.Javascript.Calls.Where(call => call.Name == "alertGraph.zoom"),
            call => Assert.Equal(1.25, call.Arguments[1]));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PendingGraphInitializationKeepsLatestFilterChangeAndBlocksOverlappingControls(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        var records = new[] { Record() };
        await fixture.Show(records);
        fixture.Javascript.BlockNextCall = true;
        var pending = fixture.Invoke("OnAfterRenderAsync", true);
        await fixture.Javascript.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var calls = fixture.Javascript.Calls.Count;
            await fixture.Set("filterText", "No-Matching-Scope");
            await fixture.Invoke("GraphFilterChanged");
            await fixture.Invoke("RetryGraphAsync");
            await fixture.Invoke("ZoomAsync", 0.8);
            await fixture.Invoke("ResetGraphAsync");
            await fixture.Invoke("OnAfterRenderAsync", false);
            Assert.Equal(calls, fixture.Javascript.Calls.Count);
            Assert.True(fixture.Get<bool>("graphNeedsInitialization"));
            Assert.Contains(fixture.Text["Graph.EmptyFiltered"], fixture.Decoded);
        }
        finally
        {
            fixture.Javascript.ReleaseCall.SetResult();
            await pending;
        }
        Assert.False(fixture.Get<bool>("graphInitialized"));
        await fixture.Invoke("OnAfterRenderAsync", false);
        Assert.True(fixture.Get<bool>("graphInitialized"));
        Assert.False(fixture.Get<bool>("graphNeedsInitialization"));
        Assert.Same(records, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
        Assert.Equal("No-Matching-Scope", fixture.Get<string>("filterText"));
        Assert.Equal(2, fixture.Javascript.Calls.Count(call => call.Name == "alertGraph.initialize"));
    }

    [Theory]
    [InlineData("en-US", "ZoomAsync")]
    [InlineData("de-DE", "ZoomAsync")]
    [InlineData("en-US", "ResetGraphAsync")]
    [InlineData("de-DE", "ResetGraphAsync")]
    public async Task BusyGraphViewChangesBlockFurtherBrowserCallsUntilCompletion(string culture, string operation)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        await fixture.Invoke("OnAfterRenderAsync", true);
        var records = fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts");
        fixture.Javascript.BlockNextCall = true;
        var pending = operation == "ZoomAsync" ? fixture.Invoke(operation, 0.8) : fixture.Invoke(operation);
        await fixture.Javascript.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var calls = fixture.Javascript.Calls.Count;
            await fixture.Invoke("ZoomAsync", 1.25);
            await fixture.Invoke("ResetGraphAsync");
            await fixture.Invoke("RetryGraphAsync");
            await fixture.Invoke("OnAfterRenderAsync", false);
            Assert.Equal(calls, fixture.Javascript.Calls.Count);
            Assert.True(fixture.Get<bool>("isGraphBusy"));
            Assert.All(Regex.Matches(fixture.Html, @"<button[^>]*title=""[^""]*""[^>]*>").Cast<Match>(),
                button => Assert.Contains("disabled", button.Value));
        }
        finally
        {
            fixture.Javascript.ReleaseCall.SetResult();
            await pending;
        }
        Assert.False(fixture.Get<bool>("isGraphBusy"));
        Assert.Same(records, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
        Assert.Null(fixture.Get<string?>("graphError"));
        await fixture.Invoke("ZoomAsync", 1.25);
        Assert.Equal(3, fixture.Javascript.Calls.Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RequiredGraphRuleFailureKeepsTimestampRecordsAndFreshnessUntilSuccessfulReload(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        await fixture.Show([Record()]);
        var records = fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts");
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertRules\" RENAME TO \"UnavailableRules\";");
        try
        {
            await fixture.Invoke("ReloadAlertsAsync");
            Assert.Same(records, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
            Assert.Equal(Now, fixture.Get<DateTimeOffset>("currentTimeUtc"));
            Assert.Equal(TimeSpan.FromMinutes(10), fixture.Get<TimeSpan>("currentHeartbeatAge"));
            Assert.Contains(fixture.Text["Graph.LoadFailed"], fixture.Decoded);
            Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is not null);
        }
        finally
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableRules\" RENAME TO \"AlertRules\";");
        }
        await fixture.Invoke("ReloadAlertsAsync");
        Assert.Null(fixture.Get<string?>("loadError"));
        Assert.True(fixture.Get<DateTimeOffset>("currentTimeUtc") > Now);
        Assert.NotSame(records, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DisposingGraphCancelsPendingBrowserOperationAndCleansPartialInitializationOnce(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        var records = fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts");
        fixture.Javascript.BlockNextCall = true;
        var pending = fixture.Invoke("OnAfterRenderAsync", true);
        await fixture.Javascript.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.DisposePage();
        await pending;
        Assert.Same(records, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
        Assert.Null(fixture.Get<string?>("graphError"));
        Assert.Empty(fixture.Logger.Entries);
        Assert.False(fixture.Get<bool>("graphInitialized"));
        Assert.Single(fixture.Javascript.Calls, call => call.Name == "alertGraph.dispose");
        var calls = fixture.Javascript.Calls.Count;
        await fixture.Invoke("RetryGraphAsync");
        await fixture.Invoke("OnAfterRenderAsync", false);
        await fixture.Invoke("ZoomAsync", 0.8);
        await fixture.Invoke("ResetGraphAsync");
        await fixture.DisposePage();
        Assert.Equal(calls, fixture.Javascript.Calls.Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task GraphDisposeFailuresAreLoggedAndExpectedDisconnectDoesNotEscape(string culture)
    {
        using var selection = new UiSelection(culture);
        foreach (var failure in new Exception[]
        {
            new JSException("PRIVATE_BROWSER_DETAIL"),
            new InvalidOperationException("PRIVATE_BROWSER_DETAIL"),
            new JSDisconnectedException("PRIVATE_BROWSER_DETAIL")
        })
        {
            await using var fixture = await Fixture.Create();
            await fixture.Render<Graph>();
            fixture.Javascript.Failure = new JSException("PRIVATE_INITIALIZATION_DETAIL");
            await fixture.Invoke("OnAfterRenderAsync", true);
            Assert.False(fixture.Get<bool>("graphInitialized"));
            fixture.Javascript.Failure = failure;
            await fixture.DisposePage();
            Assert.Single(fixture.Javascript.Calls, call => call.Name == "alertGraph.dispose");
            if (failure is not JSDisconnectedException)
                Assert.Contains(fixture.Logger.Entries, entry =>
                    entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
            else
                Assert.DoesNotContain(fixture.Logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
            Assert.DoesNotContain("PRIVATE_BROWSER_DETAIL", fixture.Html);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task GraphDisposalCancelsActiveAndQueuedRequiredReloadWithoutPublishingNewState(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Graph>();
        var records = fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts");
        var time = fixture.Get<DateTimeOffset>("currentTimeUtc");
        fixture.Factory.BlockNextCreation = true;
        var pending = fixture.Invoke("ReloadAlertsAsync");
        await fixture.Factory.CreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = fixture.Invoke("ReloadAlertsAsync");
        await fixture.DisposePage();
        await Task.WhenAll(pending, queued);
        Assert.True(fixture.Factory.CancellationObserved);
        Assert.Same(records, fixture.Get<IReadOnlyList<AlertGraphRecord>>("alerts"));
        Assert.Equal(time, fixture.Get<DateTimeOffset>("currentTimeUtc"));
        Assert.Null(fixture.Get<string?>("loadError"));
        Assert.Empty(fixture.Logger.Entries);
    }

    private sealed class GraphFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public bool BlockNextCreation { get; set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource CreationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCreation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AlertDbContext CreateDbContext() => new(options);
        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (BlockNextCreation)
            {
                BlockNextCreation = false;
                CreationStarted.SetResult();
                try { await ReleaseCreation.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            return CreateDbContext();
        }
    }

    private sealed class Fixture(DatabaseTransactionsTests.TestDatabase database, WebApplication app,
        AsyncServiceScope scope, Renderer renderer) : IAsyncDisposable
    {
        public DatabaseTransactionsTests.TestDatabase Database { get; } = database;
        public GraphFactory Factory => (GraphFactory)scope.ServiceProvider.GetRequiredService<IDbContextFactory<AlertDbContext>>();
        public AlertGraphOptions Options => scope.ServiceProvider.GetRequiredService<AlertGraphOptions>();
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        public Javascript Javascript => scope.ServiceProvider.GetRequiredService<Javascript>();
        public RecordingLogger Logger => scope.ServiceProvider.GetRequiredService<RecordingLogger>();
        public Graph Page => scope.ServiceProvider.GetRequiredService<Activator>().Page;
        public Task DisposePage() => renderer.Dispatcher.InvokeAsync(async () =>
            await Page.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        private HtmlRootComponent root;
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public T Get<T>(string field) => (T)typeof(Graph).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page)!;
        public Task Set(string field, object value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(Graph).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);
            Changed();
        });
        public Task Invoke(string method, params object[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(Graph).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, arguments);
            if (result is Task task) await task;
            Changed();
        });
        private void Changed() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, null);
        public async Task Render<T>(ParameterView? parameters = null) where T : IComponent =>
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                root = renderer.BeginRenderingComponent(typeof(T), parameters ?? ParameterView.Empty);
                await root.QuiescenceTask;
            });
        public async Task Show(IReadOnlyList<AlertGraphRecord> records)
        {
            await Set("currentTimeUtc", Now);
            await Set("currentHeartbeatAge", TimeSpan.FromMinutes(10));
            await Set("alerts", records);
        }
        public static async Task<Fixture> Create(string environment = "Production", int days = 7, bool sLocations = true)
        {
            var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
            await using (var context = database.CreateDbContext())
            {
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(statement => new[] { "Alerts", "ParsedAlerts", "ComputerInventory", "AlertRules" }.Any(name =>
                        statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("{}", "{{}}", StringComparison.Ordinal).Replace("N'", "'", StringComparison.Ordinal));
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents();
            builder.Services.AddUiLocalization();
            builder.Services.AddScoped<NavigationManager, TestNavigation>();
            using var connectionContext = database.CreateDbContext();
            var options = new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlite(connectionContext.Database.GetDbConnection())
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer,
                    InboxWorkspaceLocalizationTests.InboxTimestampModelCustomizer>().Options;
            builder.Services.AddSingleton<IDbContextFactory<AlertDbContext>>(new GraphFactory(options));
            builder.Services.AddSingleton(new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()));
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            builder.Services.AddSingleton(presenter);
            builder.Services.AddSingleton(new AlertRuleEvaluator(presenter));
            builder.Services.AddSingleton<AlertStore>();
            builder.Services.AddSingleton(new AlertHistoryOptions { Days = days });
            builder.Services.AddSingleton(new AlertGraphOptions
            {
                SLocations = sLocations,
                Layer1 = [new(AlertGraphLayer.Subscription, "Original <subscription>"), new(AlertGraphLayer.Domain, "Original domain")],
                Layer2 = [new(AlertGraphLayer.ResourceGroup, "Original resource group")],
                Layer3 = [new(AlertGraphLayer.Target, "Original target")],
                DefaultLayer1 = AlertGraphLayer.Subscription, DefaultLayer2 = AlertGraphLayer.ResourceGroup,
                DefaultLayer3 = AlertGraphLayer.Target,
                SiteLocations = [new() { DisplayName = "Original <site>", Latitude = 12.25, Longitude = 34.5, Aliases = ["Original.Site"] }]
            });
            builder.Services.AddSingleton<Javascript>();
            builder.Services.AddSingleton<RecordingLogger>();
            builder.Services.AddSingleton<ILogger<Graph>>(services => services.GetRequiredService<RecordingLogger>());
            builder.Services.AddSingleton<IJSRuntime>(services => services.GetRequiredService<Javascript>());
            builder.Services.AddScoped<Activator>();
            builder.Services.AddScoped<IComponentActivator>(services => services.GetRequiredService<Activator>());
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            return new Fixture(database, app, scope, new Renderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()));
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
