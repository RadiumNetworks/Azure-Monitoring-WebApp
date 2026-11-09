using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class QueryExamplesLocalizationTests
{
    [Theory]
    [InlineData("en-US", 0, false)]
    [InlineData("de-DE", 0, false)]
    [InlineData("en-US", 1, false)]
    [InlineData("de-DE", 1, false)]
    [InlineData("en-US", 2, false)]
    [InlineData("de-DE", 2, false)]
    [InlineData("en-US", 0, true)]
    [InlineData("de-DE", 0, true)]
    public async Task PageLocalizesLoadingAndExamplesWithoutChangingApiResponses(
        string culture, int activeCount, bool recentResolution)
    {
        using var selection = new UiSelection(culture);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
            .ReplaceService<IModelCustomizer, AlertInventoryLearningTests.InventoryLearningTestModelCustomizer>().Options;
        var factory = new GatedFactory(options);
        var now = DateTimeOffset.UtcNow;
        List<AlertRecord> seeded =
        [
            Alert("old", now.AddHours(-5)),
            Alert("resolved", now.AddHours(-4)),
            Alert("resolved", recentResolution ? now.AddMinutes(-2) : now.AddHours(-3), "Resolved")
        ];
        seeded.AddRange(Enumerable.Range(0, activeCount).Select(index => Alert($"active-{index}", now.AddMinutes(-10 - index))));
        await using (var context = factory.CreateDbContext())
        {
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            context.Alerts.AddRange(seeded);
            await context.SaveChangesAsync();
        }
        async Task<string> Stored()
        {
            await using var context = factory.CreateDbContext();
            return JsonSerializer.Serialize((await context.Alerts.AsNoTracking().ToArrayAsync()).OrderBy(alert => alert.Id));
        }
        var originalStored = await Stored();
        var log = new StoreLog();
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var store = new AlertStore(factory,
            new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder()),
            presenter, new AlertRuleEvaluator(presenter), log);
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton(store).AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(new TestNavigation()).BuildServiceProvider();
        await using var renderer = new StaticRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent(typeof(Queries), ParameterView.Empty));
        try
        {
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var loading = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Contains(culture == "de-DE" ? "Wird aktualisiert..." : "Refreshing...", loading);
            Assert.Contains("disabled", Regex.Match(loading, "<button[^>]*>").Value);
            Assert.DoesNotContain("class=\"query-example\"", loading);
        }
        finally
        {
            factory.Release.TrySetResult();
            await root.QuiescenceTask;
        }
        var rawHtml = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains(culture == "de-DE" ? "<h1>Abfragebeispiele</h1>" : "<h1>Query examples</h1>", html);
        Assert.Contains("<p class=\"eyebrow\">Integration</p>", html);
        Assert.Contains(culture == "de-DE"
            ? "Direkt nutzbare API-Anfragen mit Ergebnissen aus den aktuellen Alarmdaten."
            : "Ready-to-use API requests with results from the current alert data.", html);
        Assert.Contains(culture == "de-DE" ? ">Ergebnisse aktualisieren</button>" : ">Refresh results</button>", html);
        Assert.DoesNotContain("disabled", Regex.Match(rawHtml, "<button[^>]*>").Value);
        var page = Assert.IsType<Queries>(activator.Page);
        var examples = Assert.IsAssignableFrom<IEnumerable>(typeof(Queries)
            .GetField("examples", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)).Cast<object>().ToArray();
        Assert.Equal(2, examples.Length);
        var sections = Regex.Matches(rawHtml, "<section class=\"query-example\".*?</section>", RegexOptions.Singleline);
        Assert.Equal(2, sections.Count);
        var alerts = await store.GetAllAsync();
        Assert.Equal(seeded.Count, alerts.Count);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        for (var index = 0; index < examples.Length; index++)
        {
            var response = Assert.IsType<AlertQueryResponse>(examples[index].GetType().GetProperty("Response")!.GetValue(examples[index]));
            var expectedCount = index == 0 ? activeCount : activeCount + (recentResolution ? 1 : 0);
            Assert.Equal(expectedCount, response.Count);
            Assert.Equal(index == 0 ? "active" : "events", response.Mode);
            Assert.Equal(TimeSpan.FromMinutes(120), response.To - response.From);
            Assert.Equal(TimeSpan.Zero, response.From.Offset);
            Assert.Equal(TimeSpan.Zero, response.To.Offset);
            Assert.Equal(response.To, Assert.IsType<AlertQueryResponse>(examples[0].GetType().GetProperty("Response")!.GetValue(examples[0])).To);
            var expectedAlerts = (index == 0
                ? AlertQuery.GetActiveSince(alerts, response.From)
                : AlertQuery.GetEventsSince(alerts, response.From)).Select(AlertQueryItem.FromAlert).ToArray();
            Assert.Equal(JsonSerializer.Serialize(expectedAlerts), JsonSerializer.Serialize(response.Alerts));
            var section = sections[index].Value;
            var decoded = WebUtility.HtmlDecode(section);
            Assert.Contains($"aria-labelledby=\"{(index == 0 ? "active-query" : "event-query")}\"", section);
            Assert.Contains(culture == "de-DE"
                ? index == 0 ? "<span>Aktive Alarme</span>" : "<span>Alarmereignisse</span>"
                : index == 0 ? "<span>Active alerts</span>" : "<span>Alert events</span>", decoded);
            Assert.Contains(culture == "de-DE"
                ? index == 0 ? ">Letzte 120 Minuten</h2>" : ">Letzte 2 Stunden</h2>"
                : index == 0 ? ">Last 120 minutes</h2>" : ">Last 2 hours</h2>", decoded);
            Assert.Contains(culture == "de-DE"
                ? $"<span class=\"query-count\">{expectedCount} " + (expectedCount == 1 ? "Ergebnis" : "Ergebnisse") + "</span>"
                : $"<span class=\"query-count\">{expectedCount} results</span>", decoded);
            var url = "https://monitor.example/app/api/query?" + (index == 0 ? "activeMinutes=120" : "eventHours=2");
            Assert.Contains($"href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{url}</a>", decoded);
            Assert.Contains(culture == "de-DE" ? "<strong>Antwort</strong>" : "<strong>Response</strong>", decoded);
            Assert.Contains("HTTP 200 · application/json", decoded);
            var json = WebUtility.HtmlDecode(Regex.Match(section, "<pre class=\"query-result\">(.*?)</pre>", RegexOptions.Singleline).Groups[1].Value);
            Assert.Equal(JsonSerializer.Serialize(response, serializerOptions), json);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(new[] { "mode", "from", "to", "count", "alerts" }, document.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.All(response.Alerts, item =>
            {
                Assert.Equal("Active alerts", item.AlertName);
                Assert.Equal("Original <description> &", item.Description);
                Assert.Equal("Last 2 hours", item.Comments);
            });
        }
        Assert.Equal(originalStored, await Stored());
        Assert.Empty(log.Errors);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        Assert.Equal("/queries", Assert.Single(typeof(Queries).GetCustomAttributes<RouteAttribute>()).Template);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task InitialReadFailureNeverBecomesSuccessfulEmptyApiExamples(string culture, bool configurationFailure)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await BoundaryFixture.CreateAsync(validConfiguration: !configurationFailure);
        var failure = new IOException("Private query provider <failure> & detail.");
        if (!configurationFailure) fixture.Factory.Failure = failure;
        var stored = await fixture.Stored();
        await fixture.Render();
        Assert.Empty(fixture.Examples);
        Assert.DoesNotContain("HTTP 200", fixture.Decoded);
        Assert.DoesNotContain("class=\"query-example\"", fixture.Html);
        Assert.Contains(fixture.Text["QueryExamples.LoadFailed"], fixture.Decoded);
        Assert.DoesNotContain("Private", fixture.Decoded);
        Assert.False(fixture.Get<bool>("isLoading"));
        Assert.Single(fixture.Log.Errors);
        if (!configurationFailure) Assert.Same(failure, fixture.Log.Errors[0]);
        Assert.Equal(stored, await fixture.Stored());
        fixture.UseValidConfiguration();
        await fixture.Refresh();
        Assert.Equal(2, fixture.Examples.Length);
        Assert.Null(fixture.Get<string?>("errorMessage"));
        Assert.Contains("HTTP 200 · application/json", fixture.Decoded);
        Assert.Equal(stored, await fixture.Stored());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task FailedRefreshRetainsExactConfirmedPayloadsAndNeverClaimsANewRead(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.Render();
        var examples = fixture.Get<object>("examples");
        var json = fixture.Payloads();
        var stored = await fixture.Stored();
        var failure = new IOException("Private query refresh <failure> & detail.");
        fixture.Factory.Failure = failure;
        await fixture.Refresh();
        Assert.Same(examples, fixture.Get<object>("examples"));
        Assert.Equal(json, fixture.Payloads());
        Assert.Contains(fixture.Text["QueryExamples.RefreshFailed"], fixture.Decoded);
        Assert.DoesNotContain("Private", fixture.Decoded);
        Assert.Same(failure, Assert.Single(fixture.Log.Errors));
        Assert.Equal(stored, await fixture.Stored());
        await fixture.Refresh();
        Assert.Null(fixture.Get<string?>("errorMessage"));
        Assert.Equal(2, fixture.Examples.Length);
        Assert.Equal(stored, await fixture.Stored());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RefreshRejectsOverlapAndPublishesOnlyOneConfirmedRead(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.Render();
        var original = fixture.Get<object>("examples");
        var gate = fixture.Factory.BlockNext();
        var refresh = fixture.Refresh();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Factory.LastToken.CanBeCanceled);
        var reads = fixture.Factory.Reads;
        Assert.True(fixture.Get<bool>("isLoading"));
        Assert.Contains(fixture.Text["QueryExamples.Refreshing"], fixture.Decoded);
        Assert.Contains("disabled", Regex.Match(fixture.Html, "<button[^>]*>").Value);
        await fixture.Refresh();
        Assert.Equal(reads, fixture.Factory.Reads);
        Assert.Same(original, fixture.Get<object>("examples"));
        gate.Release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reads, fixture.Factory.Reads);
        Assert.False(fixture.Get<bool>("isLoading"));
        Assert.NotSame(original, fixture.Get<object>("examples"));
        Assert.Null(fixture.Get<string?>("errorMessage"));
    }

    [Theory]
    [InlineData("en-US", false, false)]
    [InlineData("de-DE", false, false)]
    [InlineData("en-US", true, false)]
    [InlineData("de-DE", true, false)]
    [InlineData("en-US", true, true)]
    [InlineData("de-DE", true, true)]
    public async Task DisposalCancelsAndDrainsReadsWithoutLatePayloadOrErrorProjection(
        string culture, bool noncooperative, bool lateFailure)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.Render();
        var examples = fixture.Get<object>("examples");
        var json = fixture.Payloads();
        var stored = await fixture.Stored();
        var failure = new IOException("Private late query failure.");
        if (lateFailure) fixture.Factory.Failure = failure;
        var gate = fixture.Factory.BlockNext(noncooperative);
        var refresh = fixture.Refresh();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = fixture.DisposePage();
        var ownedDisposal = fixture.Get<Task>("disposeTask");
        var repeat = fixture.DisposePage();
        Assert.Same(ownedDisposal, fixture.Get<Task>("disposeTask"));
        if (noncooperative)
        {
            Assert.False(disposal.IsCompleted);
            gate.Release.SetResult();
        }
        await Task.WhenAll(refresh, disposal, repeat).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Factory.LastToken.IsCancellationRequested);
        Assert.False(fixture.Get<bool>("isLoading"));
        Assert.Same(examples, fixture.Get<object>("examples"));
        Assert.Equal(json, fixture.Payloads());
        Assert.Null(fixture.Get<string?>("errorMessage"));
        Assert.Equal(stored, await fixture.Stored());
        if (lateFailure) Assert.Same(failure, Assert.Single(fixture.Log.Errors));
        else Assert.Empty(fixture.Log.Errors);
        Assert.Throws<ObjectDisposedException>(() => fixture.Get<CancellationTokenSource>("cancellation").Token);
        var reads = fixture.Factory.Reads;
        await fixture.Refresh();
        Assert.Equal(reads, fixture.Factory.Reads);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task CancellationCallbackFailureIsLoggedAndDoesNotSkipTheReadDrain(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.Render();
        var failure = new IOException("Private query cancellation detail.");
        using var registration = fixture.Get<CancellationTokenSource>("cancellation").Token.Register(() => throw failure);
        var gate = fixture.Factory.BlockNext(noncooperative: true);
        var refresh = fixture.Refresh();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = fixture.DisposePage();
        Assert.False(disposal.IsCompleted);
        gate.Release.SetResult();
        await Task.WhenAll(refresh, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        var error = Assert.IsType<AggregateException>(Assert.Single(fixture.Log.Errors));
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        Assert.Null(fixture.Get<string?>("errorMessage"));
        Assert.Throws<ObjectDisposedException>(() => fixture.Get<CancellationTokenSource>("cancellation").Token);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ProviderTimeoutIsAVisibleReadFailureRatherThanOwnCancellation(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        var failure = new OperationCanceledException("Private provider timeout.");
        fixture.Factory.Failure = failure;
        await fixture.Render();
        Assert.False(fixture.Factory.LastToken.IsCancellationRequested);
        Assert.Empty(fixture.Examples);
        Assert.Contains(fixture.Text["QueryExamples.LoadFailed"], fixture.Decoded);
        Assert.DoesNotContain("Private", fixture.Decoded);
        Assert.Same(failure, Assert.Single(fixture.Log.Errors));
        await fixture.Refresh();
        Assert.Equal(2, fixture.Examples.Length);
        Assert.Null(fixture.Get<string?>("errorMessage"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DisposalAlsoDrainsTheInitialReadWithoutPublishingItsLateCompletion(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        var gate = fixture.Factory.BlockNext(noncooperative: true);
        var rendering = fixture.Render();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = fixture.DisposePage();
        Assert.False(disposal.IsCompleted);
        gate.Release.SetResult();
        await Task.WhenAll(rendering, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Examples);
        Assert.Null(fixture.Get<string?>("errorMessage"));
        Assert.False(fixture.Get<bool>("isLoading"));
        Assert.Throws<ObjectDisposedException>(() => fixture.Get<CancellationTokenSource>("cancellation").Token);
    }

    private sealed class BoundaryFixture(SqliteConnection connection, GatedFactory factory, ServiceProvider services,
        StaticRenderer renderer, CapturingActivator activator) : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root;
        public GatedFactory Factory => factory;
        public Queries Page => activator.Page!;
        public UiText Text => services.GetRequiredService<UiText>();
        public PageLog Log => services.GetRequiredService<PageLog>();
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public object[] Examples => Get<IEnumerable>("examples").Cast<object>().ToArray();
        public T Get<T>(string name) => (T)typeof(Queries).GetField(name, Flags)!.GetValue(Page)!;
        public string Payloads() => JsonSerializer.Serialize(Examples.Select(example =>
            (AlertQueryResponse)example.GetType().GetProperty("Response")!.GetValue(example)!));
        public Task Render() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = renderer.BeginRenderingComponent(typeof(Queries), ParameterView.Empty);
            await root.QuiescenceTask;
        });
        public Task Refresh() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(Queries).GetMethod("LoadResultsAsync", Flags)!.Invoke(Page, null)!;
            if (!Get<bool>("disposed")) typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Page, null);
        });
        public Task DisposePage() => renderer.Dispatcher.InvokeAsync(() => Page.DisposeAsync().AsTask());
        public void UseValidConfiguration() => typeof(Queries).GetProperty("AlertStore", Flags)!.SetValue(Page, Store(factory, true));
        public async Task<string> Stored()
        {
            await using var context = factory.CreateDbContext();
            return JsonSerializer.Serialize((await context.Alerts.AsNoTracking().ToArrayAsync()).OrderBy(alert => alert.Id));
        }
        public static async Task<BoundaryFixture> CreateAsync(bool validConfiguration = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, AlertInventoryLearningTests.InventoryLearningTestModelCustomizer>().Options;
            var factory = new GatedFactory(options);
            factory.Release.SetResult();
            await using (var context = factory.CreateDbContext())
            {
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                        .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                        .Replace("N'", "'", StringComparison.Ordinal));
                context.Alerts.Add(Alert("original", DateTimeOffset.UtcNow.AddMinutes(-10)));
                await context.SaveChangesAsync();
            }
            var activator = new CapturingActivator();
            var log = new PageLog();
            var services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton(Store(factory, validConfiguration)).AddSingleton<IComponentActivator>(activator)
                .AddSingleton(log).AddSingleton<ILogger<Queries>>(log)
                .AddSingleton<NavigationManager>(new TestNavigation()).BuildServiceProvider();
            return new(connection, factory, services, new StaticRenderer(services,
                services.GetRequiredService<ILoggerFactory>()), activator);
        }
        private static AlertStore Store(GatedFactory factory, bool valid)
        {
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            return new(factory, new DatabaseConfigurationStatus(valid, valid ? null : "Private database configuration detail.",
                new Microsoft.Data.SqlClient.SqlConnectionStringBuilder()), presenter, new AlertRuleEvaluator(presenter), new StoreLog());
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class PageLog : ILogger<Queries>
    {
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Error) Errors.Add(exception);
        }
    }

    private static AlertRecord Alert(string id, DateTimeOffset received, string condition = "Fired") => new(
        Guid.NewGuid(), received, id, "Active alerts", "Sev2", "Original status", "Log", condition,
        "Original <target> &", "Original-rg", "Original-sub", received, "Original <description> &",
        "https://logs.example/?x=1&y=2", "Last 2 hours", "{}");

    private sealed class GatedFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        private bool block = true;
        private Func<CancellationToken, Task>? beforeNext;
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public (TaskCompletionSource Started, TaskCompletionSource Release) BlockNext(bool noncooperative = false)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeNext = async token =>
            {
                started.SetResult();
                if (noncooperative) await release.Task;
                else await release.Task.WaitAsync(token);
            };
            return (started, release);
        }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AlertDbContext CreateDbContext() => new(options);
        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            LastToken = cancellationToken;
            var before = beforeNext;
            beforeNext = null;
            var failure = Failure;
            Failure = null;
            if (block)
            {
                block = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (before is not null) await before(cancellationToken);
            if (failure is not null) throw failure;
            return CreateDbContext();
        }
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public Queries? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is Queries page) Page = page;
            return component;
        }
    }

    private sealed class StaticRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type type, int? parentId, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type)
                : throw new NotSupportedException($"Unsupported render mode: {mode.GetType().Name}");
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://monitor.example/app/", "https://monitor.example/app/queries");
    }

    private sealed class StoreLog : ILogger<AlertStore>
    {
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Error) Errors.Add(exception);
        }
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo culture = CultureInfo.CurrentCulture;
        public UiSelection(string name)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = uiCulture;
            CultureInfo.CurrentCulture = culture;
        }
    }
}
