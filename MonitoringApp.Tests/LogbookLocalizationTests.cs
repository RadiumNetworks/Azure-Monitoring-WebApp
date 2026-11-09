using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class LogbookLocalizationTests
{
    private const string Actor = "no logged-on user <actor> &";

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    public async Task PageLocalizesOwnedTextWithoutChangingCommentsAuditDataOrSaveBehavior(string culture, int count)
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousFormat = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var german = culture == "de-DE";
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, TimestampModelCustomizer>().Options);
            var contextAlert = new AlertRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "context-alert", "Original context",
                "Sev2", "Fired", "Log", "Fired", "Original <target> &", "rg", "sub", null, "", "",
                "Original fired <comment> &", "{}");
            await using (var context = factory.CreateDbContext())
            {
                await CreateSchemaAsync(context);
                context.Alerts.Add(contextAlert with { Status = "Resolved", Comments = "Current <comment> &" });
                context.AlertHistory.Add(AlertHistoryRecord.Create(contextAlert));
                for (var index = 0; index < count; index++)
                    context.LogbookEntries.Add(new()
                    {
                        Id = Guid.NewGuid(), CreatedAt = new DateTimeOffset(2026, 11, 6, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567 + index),
                        User = "Entries <user> &", Comment = "Comment <original> &\r\nOriginal continuation"
                    });
                await context.SaveChangesAsync();
            }
            var store = new LogbookStore(factory);
            async Task<string> Snapshot() => JsonSerializer.Serialize(await store.GetAllAsync());
            var original = await Snapshot();
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            var alerts = new AlertStore(factory, new(true, null, new SqlConnectionStringBuilder()), presenter,
                new(presenter), NullLogger<AlertStore>.Instance);
            var activator = new CapturingActivator();
            var logger = new RecordingLogger<Components.Pages.Logbook>();
            await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton<IComponentActivator>(activator).AddSingleton(store).AddSingleton(alerts).AddSingleton(presenter)
                .AddSingleton<ILogger<Components.Pages.Logbook>>(logger)
                .AddSingleton(new ApplicationAuthenticationOptions { Type = "sql" })
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor())).BuildServiceProvider();
            await using var renderer = new PageRenderer(services, services.GetRequiredService<ILoggerFactory>());
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(Host), ParameterView.Empty);
                await root.QuiescenceTask;
                var page = Assert.IsType<Components.Pages.Logbook>(activator.Page);
                var type = page.GetType();
                void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(page, value);
                object? Get(string name) => type.GetField(name, flags)!.GetValue(page);
                Task Call(string name) => (Task)type.GetMethod(name, flags)!.Invoke(page, [])!;
                string Render()
                {
                    type.GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                    return root.ToHtmlString();
                }
                Assert.Null(Get("errorMessage"));
                Assert.Equal(Actor, Get("currentUser"));
                var snapshots = Assert.IsAssignableFrom<IReadOnlyDictionary<Guid, AlertRecord>>(Get("alertSnapshots"));
                Assert.Equal(contextAlert.Comments, snapshots[contextAlert.Id].Comments);
                Assert.Equal(contextAlert.Status, snapshots[contextAlert.Id].Status);
                var loaded = Assert.IsAssignableFrom<IReadOnlyList<LogbookEntry>>(Get("entries"));
                Assert.Equal(count, loaded.Count);
                foreach (var loading in new[] { false, true })
                foreach (var saving in new[] { false, true })
                foreach (var user in new[] { "", Actor })
                foreach (var (draft, length) in new[] { ("", 0), ("a", 1), ("é<>&\r\n😀", 8), (new string('c', 4000), 4000) })
                {
                    Set("isLoading", loading);
                    Set("isSaving", saving);
                    Set("currentUser", user);
                    Set("commentDraft", draft);
                    var raw = Render();
                    var html = WebUtility.HtmlDecode(raw);
                    Assert.Contains(german ? "<h1>Logbuch</h1>" : "<h1>Logbook</h1>", html);
                    Assert.Contains(german
                        ? "Betriebsnotizen mit automatischem UTC-Zeitstempel und dem angemeldeten Benutzernamen erfassen."
                        : "Record operational notes with an automatic UTC timestamp and your logged-on username.", html);
                    Assert.Contains(german ? ">Neuer Eintrag</p>" : ">New entry</p>", html);
                    Assert.Contains(german ? ">Kommentar schreiben</h2>" : ">Write a comment</h2>", html);
                    Assert.Contains(user.Length == 0
                        ? german ? "<span>Schreiben ohne angemeldeten Benutzer</span>" : "<span>Writing as no logged-on user</span>"
                        : german ? $"<span>Schreiben als {Actor}</span>" : $"<span>Writing as {Actor}</span>", html);
                    Assert.Contains(german ? ">Kommentar</span>" : ">Comment</span>", html);
                    Assert.Contains(german ? $"{length} / 4000 Zeichen. Datum und Benutzer werden automatisch ergänzt."
                        : $"{length} / 4000 characters. Date and user are added automatically.", html);
                    Assert.Contains(german ? ">Einträge</h2>" : ">Entries</h2>", html);
                    Assert.Contains(german ? $"<span>{count} {(count == 1 ? "Eintrag" : "Einträge")}</span>"
                        : $"<span>{count} {(count == 1 ? "entry" : "entries")}</span>", html);
                    var textarea = Regex.Match(raw, "<textarea\\b([^>]*)>(.*?)</textarea>", RegexOptions.Singleline);
                    Assert.Contains("required", textarea.Groups[1].Value);
                    Assert.Contains("maxlength=\"4000\"", textarea.Groups[1].Value);
                    Assert.Contains("rows=\"5\"", textarea.Groups[1].Value);
                    Assert.Equal(draft, WebUtility.HtmlDecode(textarea.Groups[2].Value));
                    var buttons = Regex.Matches(raw, "<button\\b([^>]*)>(.*?)</button>", RegexOptions.Singleline);
                    Assert.Equal(2, buttons.Count);
                    Assert.Equal(loading, buttons[0].Groups[1].Value.Contains("disabled"));
                    Assert.Equal(saving || user.Length == 0, buttons[1].Groups[1].Value.Contains("disabled"));
                    Assert.Equal(loading ? (german ? "Wird aktualisiert..." : "Refreshing...") : (german ? "Aktualisieren" : "Refresh"),
                        WebUtility.HtmlDecode(buttons[0].Groups[2].Value).Trim());
                    Assert.Equal(saving ? (german ? "Wird gespeichert..." : "Saving...") : (german ? "Zum Logbuch hinzufügen" : "Add to logbook"),
                        WebUtility.HtmlDecode(buttons[1].Groups[2].Value).Trim());
                    Assert.Equal(loading && count == 0, html.Contains(german ? "Logbucheinträge werden geladen..." : "Loading logbook entries..."));
                    Assert.Equal(!loading && count == 0, html.Contains(german ? "Das Logbuch enthält noch keine Einträge." : "The logbook does not contain any entries yet."));
                    var rows = Regex.Matches(raw, "<tbody>.*?</tbody>", RegexOptions.Singleline);
                    Assert.Equal(count > 0 ? 1 : 0, rows.Count);
                    if (count > 0)
                    {
                        Assert.Contains(german ? "<th>Datum/Uhrzeit (UTC)</th>" : "<th>Datetime (UTC)</th>", html);
                        Assert.Contains(german ? "<th>Benutzer</th>" : "<th>User</th>", html);
                        Assert.Contains(german ? "<th>Kommentar</th>" : "<th>Comment</th>", html);
                        Assert.Equal(count, Regex.Matches(rows[0].Value, "<tr>").Count);
                        foreach (var entry in loaded)
                        {
                            Assert.Contains($"datetime=\"{entry.CreatedAt:O}\"", WebUtility.HtmlDecode(raw));
                            Assert.Contains(">2026-11-06 12:34:56</time>", html);
                            Assert.Contains(entry.Comment, html);
                        }
                        Assert.Contains("Entries &lt;user&gt; &amp;", raw);
                        Assert.Contains("Comment &lt;original&gt; &amp;", raw);
                    }
                }
                Assert.Equal(original, await Snapshot());
                await CheckFailurePhases();
                loaded = Assert.IsAssignableFrom<IReadOnlyList<LogbookEntry>>(Get("entries"));
                if (count > 0)
                {
                    var alert = new AlertRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "original", "Comment", "Sev2", "Fired",
                        "Log", "Fired", "Original <target> &", "rg", "sub", null, "", "", "", "{}");
                    loaded[0].AlertRecordId = alert.Id;
                    Set("alertSnapshots", new Dictionary<Guid, AlertRecord> { [alert.Id] = alert });
                    var raw = Render();
                    Assert.Contains(german ? "<b>Suchergebnisse</b>" : "<b>Search Results</b>", WebUtility.HtmlDecode(raw));
                    Assert.Contains("Original &lt;target&gt; &amp; - Comment", raw);
                    Assert.Contains("<span class=\"search-result-empty\">-</span>", raw);
                    Assert.Equal(original, await Snapshot());
                    return;
                }
                Set("isLoading", false);
                Set("isSaving", true);
                Set("currentUser", Actor);
                Set("commentDraft", "Original <busy> &");
                Set("statusMessage", "Original <status> &");
                Set("errorMessage", "Original <error> &");
                await Call("SaveAsync");
                Assert.Equal("Original <busy> &", Get("commentDraft"));
                Assert.Equal("Original <status> &", Get("statusMessage"));
                Assert.Equal("Original <error> &", Get("errorMessage"));
                Assert.Equal(original, await Snapshot());
                Set("isSaving", false);
                Set("currentUser", Actor);
                Set("commentDraft", "Original <save> &");
                factory.OnCreate = () => throw new InvalidOperationException("Original <storage> &");
                try { await Call("SaveAsync"); }
                finally { factory.OnCreate = null; }
                Assert.Equal(services.GetRequiredService<UiText>()["Logbook.Errors.ChangeUnconfirmed"], Get("errorMessage"));
                Assert.Null(Get("statusMessage"));
                Assert.Equal("Original <save> &", Get("commentDraft"));
                Assert.DoesNotContain("Original &lt;storage&gt; &amp;", Render());
                var commentText = new string('a', 3992) + "é<>&\r\n😀";
                Assert.Equal(4000, commentText.Length);
                Set("commentDraft", commentText);
                var before = DateTimeOffset.UtcNow;
                await Call("SaveAsync");
                var after = DateTimeOffset.UtcNow;
                Assert.Null(Get("errorMessage"));
                Assert.Equal("", Get("commentDraft"));
                var success = german ? "Der Kommentar wurde zum Logbuch hinzugefügt." : "The comment was added to the logbook.";
                Assert.Equal(success, Get("statusMessage"));
                Assert.Contains(success, WebUtility.HtmlDecode(Render()));
                var saved = Assert.Single(await store.GetAllAsync());
                Assert.Equal(commentText, saved.Comment);
                Assert.Equal(Actor, saved.User);
                Assert.InRange(saved.CreatedAt, before, after);
                var savedSnapshot = await Snapshot();
                factory.OnCreate = () => throw new InvalidOperationException("Original <load> &");
                try { await Call("LoadEntriesAsync"); }
                finally { factory.OnCreate = null; }
                Assert.Equal(services.GetRequiredService<UiText>()["Logbook.LoadError"], Get("errorMessage"));
                Assert.Equal(success, Get("statusMessage"));
                Assert.DoesNotContain("Original &lt;load&gt; &amp;", Render());
                await Call("LoadEntriesAsync");
                Assert.Null(Get("errorMessage"));
                Assert.Equal(savedSnapshot, await Snapshot());
                Assert.False(Assert.IsType<bool>(Get("isSaving")));
                Assert.False(Assert.IsType<bool>(Get("isLoading")));
                var resultAlert = new AlertRecord(Guid.NewGuid(), before, "Original alert ID", "Original alert name",
                    "Sev2", "Active", "Log", "Fired", "Original target", "Original group", "Original subscription",
                    before, "", "", "", QueryResultLocalizationTests.Payload("Replication", 1));
                Set("entries", new[] { new LogbookEntry
                {
                    Id = saved.Id, CreatedAt = saved.CreatedAt, User = saved.User,
                    Comment = saved.Comment, AlertRecordId = resultAlert.Id
                } });
                Set("alertSnapshots", new Dictionary<Guid, AlertRecord> { [resultAlert.Id] = resultAlert });
                var results = WebUtility.HtmlDecode(Render());
                Assert.Contains(german ? "aria-label=\"Replikationsabfrageübersicht\"" : "aria-label=\"Replication query summary\"", results);
                Assert.Contains(german ? ">1 Verbindung</span>" : ">1 link</span>", results);
                Assert.Contains(german ? "Source <system> & {0} zu Destination <system> & {1}"
                    : "Source <system> & {0} to Destination <system> & {1}", results);
                Assert.Contains(german ? ">Letzter Erfolg</strong> 2026-11-06 12:05:09 UTC"
                    : ">Last success</strong> 2026-11-06 12:05:09 UTC", results);
                Assert.Equal(savedSnapshot, await Snapshot());

                async Task CheckFailurePhases()
                {
                    var text = services.GetRequiredService<UiText>();
                    Set("isLoading", false);
                    Set("isSaving", false);
                    foreach (var (user, comment, code, message) in new[]
                    {
                        ("", "Valid", LogbookValidationCode.UserRequired, "A logged-on user is required to write a logbook entry."),
                        (" ", "Valid", LogbookValidationCode.UserRequired, "A logged-on user is required to write a logbook entry."),
                        (new string('u', 257), "Valid", LogbookValidationCode.UserLength, "The username may contain at most 256 characters."),
                        (Actor, "", LogbookValidationCode.CommentRequired, "Enter a comment before saving."),
                        (Actor, " ", LogbookValidationCode.CommentRequired, "Enter a comment before saving."),
                        (Actor, new string('c', 4001), LogbookValidationCode.CommentLength, "The comment may contain at most 4000 characters.")
                    })
                    {
                        Set("currentUser", user);
                        Set("commentDraft", comment);
                        var created = factory.Created;
                        var error = await Assert.ThrowsAsync<LogbookValidationException>(() => store.AddAsync(user, comment));
                        Assert.Equal(code, error.Code);
                        Assert.Equal(message, error.Message);
                        Assert.Equal(created, factory.Created);
                        var logsBefore = logger.Events.Count;
                        await Call("SaveAsync");
                        Assert.Equal(created, factory.Created);
                        Assert.Equal(text["Logbook.Validation." + code], Get("errorMessage"));
                        Assert.Contains(text["Logbook.Validation." + code], WebUtility.HtmlDecode(Render()));
                        Assert.Contains(logger.Events.Skip(logsBefore), item => item.Level == LogLevel.Warning &&
                            item.Exception is LogbookValidationException known && known.Code == code);
                        Assert.Null(Get("statusMessage"));
                        Assert.Equal(comment, Get("commentDraft"));
                        Assert.False(Assert.IsType<bool>(Get("isSaving")));
                        Assert.Equal(original, await Snapshot());
                    }
                    var fault = new InvalidOperationException("Synthetic <technical logbook detail> &");
                    Set("currentUser", Actor);
                    Set("commentDraft", "Original <failed comment>");
                    var beforeFailureLogs = logger.Events.Count;
                    factory.OnCreate = () => throw fault;
                    try { await Call("SaveAsync"); }
                    finally { factory.OnCreate = null; }
                    Assert.Equal(text["Logbook.Errors.ChangeUnconfirmed"], Get("errorMessage"));
                    Assert.Equal("Original <failed comment>", Get("commentDraft"));
                    Assert.Null(Get("statusMessage"));
                    Assert.DoesNotContain(fault.Message, WebUtility.HtmlDecode(Render()));
                    Assert.Contains(logger.Events.Skip(beforeFailureLogs),
                        item => item.Level == LogLevel.Error && ReferenceEquals(item.Exception, fault));
                    Assert.Equal(original, await Snapshot());
                    await using (var context = factory.CreateDbContext())
                        await context.Database.ExecuteSqlRawAsync(
                            "CREATE TRIGGER FailLogbookUi BEFORE INSERT ON LogbookEntries BEGIN SELECT RAISE(ABORT, 'synthetic logbook database failure'); END;");
                    var beforeDatabaseLogs = logger.Events.Count;
                    try { await Call("SaveAsync"); }
                    finally
                    {
                        await using var context = factory.CreateDbContext();
                        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailLogbookUi;");
                    }
                    Assert.Equal(text["Logbook.Errors.ChangeUnconfirmed"], Get("errorMessage"));
                    Assert.Contains(logger.Events.Skip(beforeDatabaseLogs), item => item.Level == LogLevel.Error &&
                        item.Exception is DbUpdateException { InnerException: SqliteException });
                    Assert.Equal("Original <failed comment>", Get("commentDraft"));
                    Assert.False(Assert.IsType<bool>(Get("isSaving")));
                    Assert.DoesNotContain("synthetic logbook database failure", Render());
                    Assert.Equal(original, await Snapshot());

                    foreach (var source in new[] { 1, 2, 3, 4 })
                    {
                        var request = 0;
                        var beforeLoadLogs = logger.Events.Count;
                        var cachedEntries = JsonSerializer.Serialize(Get("entries"));
                        var cachedSnapshots = JsonSerializer.Serialize(Get("alertSnapshots"));
                        var cachedInventory = JsonSerializer.Serialize(Get("computerInventory"));
                        factory.OnCreate = () => { if (++request == source) throw fault; };
                        try { await Call("LoadEntriesAsync"); }
                        finally { factory.OnCreate = null; }
                        Assert.Equal(text["Logbook.LoadError"], Get("errorMessage"));
                        Assert.Equal(cachedEntries, JsonSerializer.Serialize(Get("entries")));
                        Assert.Equal(cachedSnapshots, JsonSerializer.Serialize(Get("alertSnapshots")));
                        Assert.Equal(cachedInventory, JsonSerializer.Serialize(Get("computerInventory")));
                        Assert.Contains(logger.Events.Skip(beforeLoadLogs),
                            item => item.Level == LogLevel.Error && ReferenceEquals(item.Exception, fault));
                        Assert.False(Assert.IsType<bool>(Get("isLoading")));
                        Assert.DoesNotContain(fault.Message, WebUtility.HtmlDecode(Render()));
                        Assert.Equal(original, await Snapshot());
                    }
                    foreach (var source in new[] { 1, 2, 3, 4 })
                    {
                        var writes = 0;
                        Set("currentUser", " " + new string('u', 256) + " ");
                        Set("commentDraft", "  Committed <comment> &  ");
                        factory.AfterWrite = () =>
                        {
                            writes++;
                            var request = 0;
                            factory.OnCreate = () => { if (++request == source) throw fault; };
                        };
                        var beforeCommit = DateTimeOffset.UtcNow;
                        var beforeCommitLogs = logger.Events.Count;
                        try { await Call("SaveAsync"); }
                        finally { factory.OnCreate = null; factory.AfterWrite = null; }
                        var afterCommit = DateTimeOffset.UtcNow;
                        Assert.Equal(1, writes);
                        Assert.Equal("", Get("commentDraft"));
                        Assert.Equal(text["Logbook.Status.Added"], Get("statusMessage"));
                        Assert.Equal(text["Logbook.LoadError"], Get("errorMessage"));
                        Assert.Contains(text["Logbook.Status.Added"], WebUtility.HtmlDecode(Render()));
                        Assert.Contains(text["Logbook.LoadError"], WebUtility.HtmlDecode(Render()));
                        Assert.DoesNotContain(fault.Message, WebUtility.HtmlDecode(Render()));
                        Assert.Contains(logger.Events.Skip(beforeCommitLogs),
                            item => item.Level == LogLevel.Error && ReferenceEquals(item.Exception, fault));
                        Assert.False(Assert.IsType<bool>(Get("isSaving")));
                        Assert.False(Assert.IsType<bool>(Get("isLoading")));
                        var committed = Assert.Single(await store.GetAllAsync(), entry => entry.Comment == "Committed <comment> &");
                        Assert.Equal(new string('u', 256), committed.User);
                        Assert.Null(committed.AlertRecordId);
                        Assert.InRange(committed.CreatedAt, beforeCommit, afterCommit);
                        await Call("LoadEntriesAsync");
                        Assert.Null(Get("errorMessage"));
                        Assert.Contains("Committed &lt;comment&gt; &amp;", Render());
                        Assert.Equal(count + 1, (await store.GetAllAsync()).Count);
                        await using (var context = factory.CreateDbContext())
                        {
                            context.LogbookEntries.Remove(committed);
                            await context.SaveChangesAsync();
                        }
                        Assert.Equal(original, await Snapshot());
                        await Call("LoadEntriesAsync");
                    }
                    Set("statusMessage", null);
                    Set("currentUser", Actor);
                    Set("commentDraft", "");
                }
            });
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }

    public sealed class Host : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddAttribute(1, "Value", Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, Actor)], "test")))));
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<Components.Pages.Logbook>(0);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public Components.Pages.Logbook? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is Components.Pages.Logbook page) Page = page;
            return component;
        }
    }

    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Unsupported test render mode: {renderMode.GetType().Name}");
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public Action? OnCreate { get; set; }
        public Action? AfterWrite { get; set; }
        public int Created { get; private set; }
        public AlertDbContext CreateDbContext()
        {
            Created++;
            OnCreate?.Invoke();
            return new(new DbContextOptionsBuilder<AlertDbContext>(options).AddInterceptors(new AfterWriteInterceptor(this)).Options);
        }
    }

    private sealed class AfterWriteInterceptor(Factory factory) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            factory.AfterWrite?.Invoke();
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredAlertReadsPreserveDataAndPropagateDatabaseFailures(bool history)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
            .ReplaceService<IModelCustomizer, TimestampModelCustomizer>().Options);
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var logger = new RecordingLogger<AlertStore>();
        var store = new AlertStore(factory, new(true, null, new SqlConnectionStringBuilder()), presenter, new(presenter), logger);
        Task<IReadOnlyList<AlertRecord>> Read(bool required) => (history, required) switch
        {
            (true, true) => store.GetFiredHistoryRequiredAsync(),
            (true, false) => store.GetFiredHistoryAsync(),
            (false, true) => store.GetAllRequiredAsync(),
            _ => store.GetAllAsync()
        };
        await using (var context = factory.CreateDbContext())
            await CreateSchemaAsync(context);
        Assert.Empty(await Read(false));
        Assert.Empty(await Read(true));
        var older = new AlertRecord(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), "older", "Original <alert> &",
            "Sev2", "Fired", "Log", "Fired", "Original <target> &", "rg", "sub", null, "Original description", "",
            "Original <comment> &", "{}");
        var newest = older with { Id = Guid.NewGuid(), AlertId = "newest", ReceivedAt = older.ReceivedAt.AddMinutes(1) };
        await using (var context = factory.CreateDbContext())
        {
            context.Alerts.AddRange(older, newest);
            context.AlertHistory.AddRange(AlertHistoryRecord.Create(older), AlertHistoryRecord.Create(newest));
            await context.SaveChangesAsync();
        }
        var expected = new[] { newest, older }.Select(alert => presenter.ResolveDisplayIdentity(alert)).ToArray();
        var legacy = await Read(false);
        var requiredResult = await Read(true);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(legacy));
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(requiredResult));
        Assert.Empty(logger.Events);

        await using (var context = factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(history
                ? "ALTER TABLE \"AlertHistory\" RENAME TO \"UnavailableAlertRead\";"
                : "ALTER TABLE \"ParsedAlerts\" RENAME TO \"UnavailableAlertRead\";");
        try
        {
            Assert.Empty(await Read(false));
            Assert.Contains(logger.Events, item => item.Level == LogLevel.Error && item.Exception is SqliteException);
            var fault = await Assert.ThrowsAsync<SqliteException>(() => Read(true));
            Assert.Contains(logger.Events, item => item.Level == LogLevel.Error && ReferenceEquals(item.Exception, fault));
        }
        finally
        {
            await using var context = factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync(history
                ? "ALTER TABLE \"UnavailableAlertRead\" RENAME TO \"AlertHistory\";"
                : "ALTER TABLE \"UnavailableAlertRead\" RENAME TO \"ParsedAlerts\";");
        }
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await Read(true)));
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await Read(false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredAlertReadsRejectInvalidConfigurationWithoutChangingLegacyFallback(bool history)
    {
        var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite("Data Source=:memory:").Options);
        var configuration = DatabaseConfigurationStatus.Parse("", requireManagedIdentity: false);
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var logger = new RecordingLogger<AlertStore>();
        var store = new AlertStore(factory, configuration, presenter, new(presenter), logger);
        Assert.Empty(await (history ? store.GetFiredHistoryAsync() : store.GetAllAsync()));
        Assert.Contains(logger.Events, item => item.Level == LogLevel.Error &&
            item.Message.Contains(configuration.Error!, StringComparison.Ordinal));
        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            history ? store.GetFiredHistoryRequiredAsync() : store.GetAllRequiredAsync());
        Assert.Equal(configuration.Error, fault.Message);
        Assert.Equal(0, factory.Created);
    }

    private static async Task CreateSchemaAsync(AlertDbContext context)
    {
        foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
            await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                .Replace("N'", "'", StringComparison.Ordinal));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Enqueue((logLevel, exception, formatter(state, exception)));
    }

    public sealed class TimestampModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AlertRecord>().Property(alert => alert.ReceivedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<AlertHistoryRecord>().Property(alert => alert.ReceivedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<LogbookEntry>().Property(entry => entry.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }
    }
}
