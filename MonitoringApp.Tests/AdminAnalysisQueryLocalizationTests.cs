using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class AdminAnalysisQueryLocalizationTests
{
    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task VisualizationSelectionLoadsTemplatesWithoutSavingOrChangingOtherDraftFields(string culture, bool existing)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        if (existing) await fixture.InvokeAsync("BeginEdit", fixture.Query);
        else await fixture.InvokeAsync("BeginCreate");
        var draft = Assert.IsType<AnalysisQueryDefinition>(fixture.Field("editing"));
        if (!existing) draft.Key = "new-unsaved-query";
        var original = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync())));
        var kql = draft.Kql;
        var name = draft.Name;
        var unit = draft.Unit;
        var workspaces = draft.Workspaces.Select(item => item.WorkspaceKey).ToArray();
        if (existing)
            Assert.Equal(AnalysisVisualization.FormatForEditing(fixture.Query.VisualizationJson), draft.VisualizationJson);
        else
            Assert.Equal(AnalysisVisualization.TemplateForEditing(draft.VisualizationType), draft.VisualizationJson);
        fixture.Set("testWorkspaceKey", "original-workspace");
        Assert.IsType<HashSet<string>>(fixture.Field("testSystemKeys")).Add(
            fixture.System.SubscriptionId + "\u001f" + fixture.System.Computer);
        await fixture.InvokeAsync("TestAsync");
        Assert.NotNull(fixture.Field("testPreview"));
        var writes = fixture.Writes.Attempts;
        var calls = fixture.Client.Calls;
        var initialType = draft.VisualizationType;
        foreach (var type in AnalysisQueryVisualizationTypes.All.Where(type => type != initialType).Append(initialType))
        {
            fixture.Set("testError", "Previous diagnostic");
            await fixture.InvokeAsync("ChangeVisualization", type);
            Assert.Equal(type, draft.VisualizationType);
            Assert.Equal(AnalysisVisualization.TemplateForEditing(type), draft.VisualizationJson);
            var html = await fixture.HtmlAsync();
            Assert.Contains($"value=\"{type}\" selected", html);
            Assert.Contains(draft.VisualizationJson, html);
            Assert.Contains(fixture.Text.Format("AdminAnalysisQueries.VisualizationHelp"), html);
            Assert.Null(fixture.Field("testPreview"));
            Assert.Null(fixture.Field("testResult"));
            Assert.Null(fixture.Field("testError"));
        }
        const string custom = """{"color":"purple","decimals":3}""";
        draft.VisualizationJson = custom;
        await fixture.InvokeAsync("ChangeVisualization", draft.VisualizationType);
        Assert.Equal(custom, draft.VisualizationJson);
        await fixture.InvokeAsync("ChangeVisualization", "unsupported");
        Assert.Equal(initialType, draft.VisualizationType);
        Assert.Equal(custom, draft.VisualizationJson);
        Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.FormatFailed"], fixture.Field("errorMessage"));
        Assert.Single(fixture.Log.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);
        Assert.Equal(kql, draft.Kql);
        Assert.Equal(name, draft.Name);
        Assert.Equal(unit, draft.Unit);
        Assert.Equal(workspaces, draft.Workspaces.Select(item => item.WorkspaceKey));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal(calls, fixture.Client.Calls);
        Assert.Equal(original, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync()))));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task TrustedRegistryCardsEditorAndWithdrawalProjectWithoutChangingBoundOriginals(string culture, bool trusted)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        var entry = QueryTemplateCatalog.GetActive("kql/ad/ad-fsmo-roles");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var stored = await context.AnalysisQueryDefinitions.SingleAsync();
            stored.StandardTextProvenanceJson = trusted ? StandardQueryTextProvenance.Create(entry) : null;
            await context.SaveChangesAsync();
        }
        await fixture.StartAsync();
        var query = Assert.Single(await fixture.Registry.ListAsync());
        var original = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query));
        var expected = trusted ? fixture.Text[entry.NameResourceKey] : query.Name;
        Assert.Contains($"<strong>{expected}</strong>", await fixture.HtmlAsync());
        await fixture.InvokeAsync("BeginEdit", query);
        var editing = Assert.IsType<AnalysisQueryDefinition>(fixture.Field("editing"));
        Assert.Equal(query.StandardTextProvenanceJson, editing.StandardTextProvenanceJson);
        var html = await fixture.HtmlAsync();
        Assert.Contains($"id=\"analysis-query-editor\">{expected}</h2>", html);
        Assert.Contains($"value=\"{query.Name}\"", html);
        Assert.Equal(query.Name, editing.Name);
        Assert.Equal(query.Description, editing.Description);
        fixture.Set("queryToUnpublish", query);
        await fixture.InvokeAsync("ClearTestResult");
        Assert.Contains($"<strong>{expected}</strong>", await fixture.HtmlAsync());
        editing.Name = "Own <unsaved> &";
        await fixture.InvokeAsync("ClearTestResult");
        Assert.Contains("id=\"analysis-query-editor\">Own <unsaved> &</h2>", await fixture.HtmlAsync());
        Assert.Equal(original, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(
            Assert.Single(await fixture.Registry.ListAsync()))));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    public async Task FrameLocalizesWithoutChangingDraftsExportPolicyOrBusyRules(string culture, int count)
    {
        var oldUiCulture = CultureInfo.CurrentUICulture;
        var oldCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new ContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using (var context = factory.CreateDbContext())
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                        .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                        .Replace("N'", "'", StringComparison.Ordinal));
            var options = new CrossTenantLogAnalyticsOptions
            {
                Workspaces = [new() { Key = "original-workspace", DisplayName = "Original <workspace> &", Domains = ["original.example"] }]
            };
            var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
            var registry = new AnalysisQueryRegistry(factory, options, runner);
            var categories = new AnalysisQueryCategoryService(factory);
            const string categoryRoot = "Original <category> &";
            await categories.CreateAsync(categoryRoot, "");
            await categories.CreateAsync("Child", categoryRoot);
            await categories.CreateAsync("Grandchild", categoryRoot + "/Child");
            await categories.CreateAsync("Empty <category> &", "");
            await categories.CreateAsync(categoryRoot + " sibling", "");
            var originalCategories = (await categories.ListAsync()).ToArray();
            for (var index = 0; index < count; index++)
            {
                var query = ActiveDirectoryAnalysisQueries.Create().First();
                query.Key = $"original-query-{index}";
                query.Name = $"Original <query {index}> &";
                query.Category = index == 1 ? categoryRoot + " sibling" : categoryRoot;
                query.Workspaces.Add(new() { WorkspaceKey = "original-workspace" });
                await registry.SaveDraftAsync(query, "admin");
            }
            async Task<string> Snapshot() => JsonSerializer.Serialize((await registry.ListAsync())
                .OrderBy(query => query.Key).Select(AnalysisQueryRegistry.CreateSnapshot));
            var original = await Snapshot();
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            var log = new StoreLog();
            var store = new AlertStore(factory,
                new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder()),
                presenter, new AlertRuleEvaluator(presenter), log);
            var activator = new CapturingActivator();
            await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton<IComponentActivator>(activator).AddSingleton(registry)
                .AddSingleton(categories).AddSingleton(options).AddSingleton(runner)
                .AddSingleton(new ApplicationAuthenticationOptions())
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor())).AddSingleton(store).BuildServiceProvider();
            await using var renderer = new StaticRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var rendered = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                            [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, SqlAuthenticationRoles.Admin)], "test")))),
                        ["ChildContent"] = (RenderFragment)(builder =>
                        {
                            builder.OpenComponent<AdminAnalysisQueries>(0);
                            builder.CloseComponent();
                        })
                    }));
                await rendered.QuiescenceTask;
                return rendered;
            });
            var page = Assert.IsType<AdminAnalysisQueries>(activator.Page);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.Null(typeof(AdminAnalysisQueries).GetField("errorMessage", flags)!.GetValue(page));
            foreach (var busy in new[] { false, true })
            foreach (var collapsed in new[] { false, true })
            {
                typeof(AdminAnalysisQueries).GetField("isBusy", flags)!.SetValue(page, busy);
                typeof(AdminAnalysisQueries).GetField("registryCollapsed", flags)!.SetValue(page, collapsed);
                await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
                var raw = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
                var html = WebUtility.HtmlDecode(raw);
                Assert.Contains(culture == "de-DE"
                    ? "Ein JSON-Dokument für eine Analyseabfrage einfügen, zum Beispiel eine Datei aus „Abfragen exportieren (ZIP)“. Falls der Schlüssel bereits existiert, diesen vor dem Speichern ändern; vorhandene Abfragen werden durch diesen Import niemals überschrieben."
                    : "Paste one Analysis query JSON document, for example a file from Export queries (ZIP). If its key already exists, change the key before saving; existing queries are never overwritten by this import.", html);
                Assert.Contains(culture == "de-DE" ? "JSON in neuen Entwurf einlesen" : "Read JSON into new draft", html);
                Assert.Contains(culture == "de-DE" ? "<h1>Analyseabfragen</h1>" : "<h1>Analysis queries</h1>", html);
                Assert.Contains(culture == "de-DE"
                    ? "Die für Operatoren verfügbare KQL erstellen, validieren, versionieren und veröffentlichen."
                    : "Author, validate, version, and publish the KQL available to operators.", html);
                var heading = Regex.Match(html, "<section class=\"admin-heading\".*?</section>", RegexOptions.Singleline).Value;
                Assert.Contains(culture == "de-DE" ? "Abfragen exportieren (ZIP)" : "Export queries (ZIP)", heading);
                Assert.Contains(culture == "de-DE" ? "AD-Abfragebibliothek installieren" : "Install AD query library", heading);
                Assert.Contains(culture == "de-DE" ? "Abfrage erstellen" : "Create query", heading);
                Assert.Equal(!busy && count > 0, heading.Contains("href=\"api/admin/analysis-queries/export\""));
                if (!busy && count > 0)
                {
                    Assert.Contains("data-enhance-nav=\"false\"", heading);
                    Assert.Contains(culture == "de-DE"
                        ? "Exportiert alle gespeicherten Entwürfe, einschließlich deaktivierter Abfragen. Nicht gespeicherte Editoränderungen sind nicht enthalten."
                        : "Exports all saved drafts, including disabled queries. Unsaved editor changes are not included.", heading);
                }
                var buttons = Regex.Matches(heading, "<button\\b[^>]*>");
                Assert.Equal(busy || count == 0 ? 3 : 2, buttons.Count);
                Assert.All(buttons.Cast<Match>().Skip(buttons.Count - 2), button => Assert.Equal(busy, button.Value.Contains("disabled")));
                if (busy || count == 0) Assert.Contains("disabled", buttons[0].Value);
                Assert.Contains(culture == "de-DE"
                    ? $"<span>{count} " + (count == 1 ? "Definition" : "Definitionen") + "</span>"
                    : $"<span>{count} definitions</span>", html);
                Assert.Contains(culture == "de-DE" ? ">Abfrageregister</h2>" : ">Query registry</h2>", html);
                Assert.Contains(collapsed
                    ? culture == "de-DE" ? ">Register aufklappen</button>" : ">Expand registry</button>"
                    : culture == "de-DE" ? ">Register minimieren</button>" : ">Minimize registry</button>", html);
                Assert.Equal(collapsed, Regex.Match(raw, "<div id=\"analysis-query-cards\"[^>]*>").Value.Contains("hidden"));
                Assert.Contains(culture == "de-DE" ? ">Eine Abfrage auswählen oder erstellen</strong>" : ">Select or create a query</strong>", html);
                Assert.Contains(culture == "de-DE"
                    ? "Nur erfolgreich validierte und veröffentlichte Abfragen werden für Operatoren verfügbar."
                    : "Only successfully validated and published queries become available to operators.", html);
                if (count > 0)
                {
                    Assert.Contains("Original &lt;query 0&gt; &amp;", raw);
                    Assert.Contains("Original &lt;category&gt; &amp;", raw);
                }
                Assert.Contains("Original &lt;workspace&gt; &amp; (original-workspace)", raw);
            }
            void SetField(string name, object? value) => typeof(AdminAnalysisQueries).GetField(name, flags)!.SetValue(page, value);
            async Task<string> RenderPage() => await renderer.Dispatcher.InvokeAsync(() =>
            {
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                return root.ToHtmlString();
            });
            var selectedWorkspaces = Assert.IsType<HashSet<string>>(typeof(AdminAnalysisQueries).GetField("bulkWorkspaceKeys", flags)!.GetValue(page));
            SetField("registryCollapsed", false);
            foreach (var busy in new[] { false, true })
            foreach (var editorOpen in new[] { false, true })
            foreach (var includeDisabled in new[] { false, true })
            foreach (var hasWorkspaces in new[] { false, true })
            foreach (var selected in new[] { false, true })
            {
                if (!hasWorkspaces && selected) continue;
                SetField("isBusy", busy);
                SetField("editing", editorOpen ? new AnalysisQueryDefinition { Name = "Original <editor> &", Workspaces = [] } : null);
                SetField("bulkIncludeDisabledQueries", includeDisabled);
                typeof(AdminAnalysisQueries).GetProperty("Options", flags)!.SetValue(page,
                    hasWorkspaces ? options : new CrossTenantLogAnalyticsOptions());
                selectedWorkspaces.Clear();
                if (selected)
                    typeof(AdminAnalysisQueries).GetMethod("ToggleBulkWorkspace", flags)!.Invoke(page,
                        ["original-workspace", new ChangeEventArgs { Value = true }]);
                var raw = await RenderPage();
                var bulk = Regex.Match(WebUtility.HtmlDecode(raw),
                    "<section class=\"analysis-category-manager\"[^>]*>.*?</section>", RegexOptions.Singleline).Value;
                Assert.Contains(culture == "de-DE" ? "aria-label=\"Allen Abfragen Workspaces zuordnen\"" : "aria-label=\"Assign workspaces to all queries\"", bulk);
                Assert.Contains(culture == "de-DE" ? "<h3>Workspace-Zuordnung für alle Abfragen</h3>" : "<h3>Workspace assignment for all queries</h3>", bulk);
                Assert.Contains(culture == "de-DE"
                    ? "Die ausgewählten Workspaces für alle gespeicherten Abfragen gleichzeitig aktivieren. Andere Workspace-Zuordnungen, Abfrageinhalte und der Versionsverlauf bleiben erhalten. Neue Zuordnungen erfordern eine Validierung; diese Aktion validiert keine Abfragen und veröffentlicht keine neuen Versionen."
                    : "Enable the selected workspaces for all saved queries at once. Other workspace assignments, query content and version history are preserved. New assignments require validation; this action does not validate queries or publish new versions.", bulk);
                Assert.Equal(editorOpen, bulk.Contains(culture == "de-DE"
                    ? "Änderungen speichern und den Abfrageeditor schließen, bevor Workspace-Zuordnungen für alle Abfragen angewendet werden."
                    : "Save your changes and close the query editor before applying bulk workspace assignments."));
                Assert.Contains(culture == "de-DE" ? "<legend>Ziel-Workspaces</legend>" : "<legend>Target workspaces</legend>", bulk);
                Assert.Equal(!hasWorkspaces, bulk.Contains(culture == "de-DE"
                    ? "Es sind keine Log-Analytics-Workspaces konfiguriert." : "No Log Analytics workspaces are configured."));
                Assert.Contains(culture == "de-DE" ? "Deaktivierte Abfragen einbeziehen und erneut aktivieren" : "Include disabled queries and re-enable them", bulk);
                Assert.Equal(includeDisabled, bulk.Contains(culture == "de-DE"
                    ? "Durch erneutes Aktivieren von Abfragen kann der Zugriff auf ihre vorhandenen veröffentlichten Versionen in zuvor validierten Workspaces wiederhergestellt werden."
                    : "Re-enabling queries can restore access to their existing published versions on previously validated workspaces."));
                Assert.Equal(busy || editorOpen, Regex.Match(bulk, "<fieldset\\b[^>]*>").Value.Contains("disabled"));
                Assert.Equal(busy || editorOpen || count == 0 || !selected, Regex.Match(bulk, "<button\\b[^>]*>").Value.Contains("disabled"));
                Assert.Contains(culture == "de-DE" ? ">Auf alle Abfragen anwenden</button>" : ">Apply to all queries</button>", bulk);
                var checkboxes = Regex.Matches(bulk, "<input\\b[^>]*>");
                Assert.Equal(hasWorkspaces ? 2 : 1, checkboxes.Count);
                Assert.Equal(includeDisabled, checkboxes[^1].Value.Contains("checked"));
                if (hasWorkspaces)
                {
                    Assert.Equal(selected, checkboxes[0].Value.Contains("checked"));
                    Assert.Contains("Original &lt;workspace&gt; &amp; (original-workspace)", raw);
                }
                Assert.Equal(selected ? ["original-workspace"] : Array.Empty<string>(), selectedWorkspaces.ToArray());
            }
            SetField("editing", null);
            typeof(AdminAnalysisQueries).GetProperty("Options", flags)!.SetValue(page, options);
            foreach (var busy in new[] { false, true })
            foreach (var rename in new[] { false, true })
            {
                SetField("isBusy", busy);
                typeof(AdminAnalysisQueries).GetMethod(rename ? "BeginCategoryEdit" : "BeginCategoryCreate", flags)!
                    .Invoke(page, [rename ? categoryRoot : categoryRoot + " sibling"]);
                var raw = await RenderPage();
                var html = WebUtility.HtmlDecode(raw);
                var manager = Regex.Matches(html, "<section class=\"analysis-category-manager\"[^>]*>.*?</section>", RegexOptions.Singleline)[1].Value;
                Assert.Contains(culture == "de-DE" ? "aria-label=\"Abfragekategorien verwalten\"" : "aria-label=\"Manage query categories\"", manager);
                Assert.Contains(culture == "de-DE" ? "<h3>Kategorien verwalten</h3>" : "<h3>Manage categories</h3>", manager);
                Assert.Contains(culture == "de-DE"
                    ? "Verschachtelte Kategorien verwenden, um das Register und die Bibliothek zu organisieren. Abfragen durch Ziehen einer Karte auf eine Kategorieüberschrift oder über deren Kategorieauswahl verschieben. Veröffentlichte Abfragen werden ohne erneutes Veröffentlichen verschoben."
                    : "Use nested categories to organize both the registry and library. Move queries by dragging a card onto a category heading or using its category selection. Published queries move without republishing.", manager);
                Assert.Contains(rename ? culture == "de-DE" ? ">Kategoriename</span>" : ">Category name</span>"
                    : culture == "de-DE" ? ">Neue Kategorie</span>" : ">New category</span>", manager);
                Assert.Contains(culture == "de-DE" ? ">Übergeordnete Kategorie</span>" : ">Parent category</span>", manager);
                Assert.Contains(culture == "de-DE" ? ">Oberste Ebene</option>" : ">Top level</option>", manager);
                Assert.Contains(rename ? culture == "de-DE" ? ">Kategorie speichern</button>" : ">Save category</button>"
                    : culture == "de-DE" ? ">Kategorie erstellen</button>" : ">Create category</button>", manager);
                Assert.Contains(culture == "de-DE" ? ">Leeren / abbrechen</button>" : ">Clear / cancel</button>", manager);
                var rawManager = Regex.Matches(raw, "<section class=\"analysis-category-manager\"[^>]*>.*?</section>", RegexOptions.Singleline)[1].Value;
                var controls = Regex.Matches(rawManager, "<(?:input|select|button)\\b[^>]*>");
                Assert.Equal(4, controls.Count);
                Assert.All(controls.Cast<Match>(), control => Assert.Equal(busy, control.Value.Contains("disabled")));
                Assert.Contains("maxlength=\"128\"", controls[0].Value);
                Assert.Contains("required", controls[0].Value);
                if (rename) Assert.Contains($"value=\"{categoryRoot}\"", WebUtility.HtmlDecode(controls[0].Value));
                var parentPaths = Regex.Matches(manager, "<option value=\"(?<path>[^\"]+)\"")
                    .Select(match => match.Groups["path"].Value).Order().ToArray();
                Assert.Equal(originalCategories.Where(path => !rename || !AnalysisQueryCategories.IsWithin(path, categoryRoot)).Order(), parentPaths);
                if (rename) Assert.DoesNotContain($"value=\"{categoryRoot} sibling\" selected", manager);
                else Assert.Contains($"value=\"{categoryRoot} sibling\" selected", manager);
                var categoryActions = Regex.Matches(raw,
                    "<summary\\b[^>]*>\\s*<strong>(?<name>.*?)</strong>.*?</summary>\\s*<div class=\"analysis-query-category-body\">\\s*<div class=\"admin-card-actions analysis-category-actions\">(?<actions>.*?)</div>",
                    RegexOptions.Singleline);
                Assert.Equal(5, categoryActions.Count);
                foreach (Match category in categoryActions)
                {
                    var actions = WebUtility.HtmlDecode(category.Groups["actions"].Value);
                    Assert.Contains(culture == "de-DE" ? ">Unterkategorie hinzufügen</button>" : ">Add subcategory</button>", actions);
                    Assert.Contains(culture == "de-DE" ? ">Umbenennen / verschieben</button>" : ">Rename / move</button>", actions);
                    Assert.Contains(culture == "de-DE" ? ">Leere Kategorie löschen</button>" : ">Delete empty category</button>", actions);
                    var buttons = Regex.Matches(actions, "<button\\b[^>]*>");
                    Assert.Equal(3, buttons.Count);
                    Assert.All(buttons.Cast<Match>().Take(2), button => Assert.Equal(busy, button.Value.Contains("disabled")));
                    var name = WebUtility.HtmlDecode(category.Groups["name"].Value);
                    Assert.Equal(busy || name == categoryRoot || name == "Child" || (count == 2 && name == categoryRoot + " sibling"),
                        buttons[2].Value.Contains("disabled"));
                }
                Assert.Contains("Original &lt;category&gt; &amp;", raw);
            }
            var cardQueries = Assert.IsAssignableFrom<IReadOnlyList<AnalysisQueryDefinition>>(
                typeof(AdminAnalysisQueries).GetField("queries", flags)!.GetValue(page));
            var cardStates = cardQueries.Select(query => (Query: query, query.Status, query.Enabled, query.PublishedVersionId)).ToArray();
            var workspaceStates = cardQueries.SelectMany(query => query.Workspaces)
                .Select(workspace => (Workspace: workspace, workspace.Enabled, workspace.LastValidationMessage)).ToArray();
            foreach (var busy in new[] { false, true })
            foreach (var status in new[] { AnalysisQueryStatuses.Draft, AnalysisQueryStatuses.Validated, AnalysisQueryStatuses.Published, AnalysisQueryStatuses.Disabled })
            foreach (var published in new[] { false, true })
            foreach (var workspaceEnabled in new[] { false, true })
            {
                SetField("isBusy", busy);
                foreach (var query in cardQueries)
                {
                    query.Status = status;
                    query.Enabled = status != AnalysisQueryStatuses.Disabled;
                    query.PublishedVersionId = published ? Guid.NewGuid() : null;
                    foreach (var workspace in query.Workspaces)
                    {
                        workspace.Enabled = workspaceEnabled;
                        workspace.LastValidationMessage = "Original <validation> &";
                    }
                }
                var raw = await RenderPage();
                var cards = Regex.Matches(raw, "<article class=\"admin-rule-card[^\"]*\".*?</article>", RegexOptions.Singleline);
                Assert.Equal(count, cards.Count);
                foreach (Match card in cards)
                {
                    var html = WebUtility.HtmlDecode(card.Value);
                    Assert.Contains(culture == "de-DE" ? ">Kategorie (wird sofort angewendet)</span>" : ">Category (applies immediately)</span>", html);
                    Assert.Contains(culture == "de-DE" ? ">Validieren</button>" : ">Validate</button>", html);
                    Assert.Contains(culture == "de-DE" ? ">Veröffentlichen</button>" : ">Publish</button>", html);
                    Assert.Contains(culture == "de-DE" ? ">Veröffentlichung zurückziehen</button>" : ">Unpublish</button>", html);
                    Assert.Contains(culture == "de-DE" ? ">Deaktivieren</button>" : ">Disable</button>", html);
                    Assert.Contains($"v0 · {status}</span>", html);
                    Assert.Contains($"draggable=\"{(busy ? "false" : "true")}\"", html);
                    Assert.Contains($"original-workspace: {(workspaceEnabled ? AnalysisQueryValidationStatuses.NotValidated : culture == "de-DE" ? "Deaktiviert" : "Disabled")}", html);
                    Assert.Contains("Original &lt;validation&gt; &amp;", card.Value);
                    var buttons = Regex.Matches(card.Value, "<button\\b[^>]*>");
                    Assert.Equal(5, buttons.Count);
                    Assert.Equal(busy, buttons[0].Value.Contains("disabled"));
                    Assert.Equal(busy, buttons[1].Value.Contains("disabled"));
                    Assert.Equal(busy || status != AnalysisQueryStatuses.Validated, buttons[2].Value.Contains("disabled"));
                    Assert.Equal(busy || !published, buttons[3].Value.Contains("disabled"));
                    Assert.Equal(busy, buttons[4].Value.Contains("disabled"));
                    Assert.Equal(busy, Regex.Match(card.Value, "<select\\b[^>]*>").Value.Contains("disabled"));
                    Assert.Equal(originalCategories.Order(), Regex.Matches(html, "<option value=\"(?<path>[^\"]+)\"")
                        .Select(match => match.Groups["path"].Value).Order());
                }
            }
            foreach (var state in cardStates)
            {
                state.Query.Status = state.Status;
                state.Query.Enabled = state.Enabled;
                state.Query.PublishedVersionId = state.PublishedVersionId;
            }
            foreach (var message in new[]
            {
                "Validated with 1 row(s).",
                "No inventory system routes to this workspace.",
                "Query failed: Original <provider> &",
                "Eigene <Validierung> &"
            })
            {
                foreach (var workspace in cardQueries.SelectMany(query => query.Workspaces))
                    workspace.LastValidationMessage = message;
                var raw = await RenderPage();
                if (count > 0)
                    Assert.Contains(message, WebUtility.HtmlDecode(raw));
                Assert.DoesNotContain("<provider>", raw);
                Assert.DoesNotContain("<Validierung>", raw);
                foreach (var workspace in cardQueries.SelectMany(query => query.Workspaces))
                    Assert.Equal(message, workspace.LastValidationMessage);
            }
            foreach (var state in workspaceStates)
            {
                state.Workspace.Enabled = state.Enabled;
                state.Workspace.LastValidationMessage = state.LastValidationMessage;
            }
            foreach (var existing in new[] { false, true })
            {
                if (existing && count == 0) continue;
                typeof(AdminAnalysisQueries).GetMethod(existing ? "BeginEdit" : "BeginCreate", flags)!
                    .Invoke(page, existing ? [cardQueries[0]] : []);
                var editor = Assert.IsType<AnalysisQueryDefinition>(typeof(AdminAnalysisQueries).GetField("editing", flags)!.GetValue(page));
                editor.Description = "Original <description> &";
                editor.VisualizationJson = "{\"color\":\"green\",\"decimals\":3}";
                foreach (var busy in new[] { false, true })
                foreach (var scope in AnalysisQueryScopes.All)
                foreach (var role in new[] { SqlAuthenticationRoles.Operator, SqlAuthenticationRoles.Admin })
                {
                    SetField("isBusy", busy);
                    editor.Scope = scope;
                    editor.MinimumRole = role;
                    var before = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(editor));
                    var raw = await RenderPage();
                    var rawEditor = Regex.Match(raw, "<section class=\"admin-editor\".*?</section>", RegexOptions.Singleline).Value;
                    var html = WebUtility.HtmlDecode(rawEditor);
                    Assert.Contains(culture == "de-DE" ? ">Entwurf</p>" : ">Draft</p>", html);
                    Assert.Contains($"<h2 id=\"analysis-query-editor\">{editor.Name}</h2>", html);
                    foreach (var (english, german) in new[]
                    {
                        ("Key", "Schlüssel"), ("Name", "Name"), ("Category path", "Kategoriepfad"), ("Unit", "Einheit"),
                        ("Query scope", "Abfragebereich"), ("Visualization", "Visualisierung"), ("Description", "Beschreibung"),
                        ("Visualization options (JSON)", "Visualisierungsoptionen (JSON)"),
                        ("Default range (minutes)", "Standardzeitraum (Minuten)"),
                        ("Maximum range (minutes)", "Maximaler Zeitraum (Minuten)"),
                        ("Bin size (minutes)", "Intervallgröße (Minuten)"), ("Minimum role", "Mindestens erforderliche Rolle")
                    })
                        Assert.Contains($">{(culture == "de-DE" ? german : english)}</span>", html);
                    Assert.Contains(culture == "de-DE"
                        ? "/ für Unterkategorien verwenden. Kategorieänderungen werden beim Speichern sofort auf die Bibliothek angewendet."
                        : "Use / for subcategories. Category changes apply to the library immediately on save.", html);
                    Assert.Contains(culture == "de-DE" ? ">Zulässige Log-Analytics-Workspaces</legend>" : ">Allowed Log Analytics workspaces</legend>", html);
                    Assert.Contains(culture == "de-DE" ? ">Als Entwurf speichern</button>" : ">Save as draft</button>", html);
                    Assert.Contains(culture == "de-DE" ? ">Abbrechen</button>" : ">Cancel</button>", html);
                    Assert.Contains(culture == "de-DE" ? ">Ausgewählte Systeme (Standard)</option>" : ">Selected systems (default)</option>", html);
                    Assert.Contains(culture == "de-DE"
                        ? ">Workspaceweit (alle Umgebungen in den zugeordneten Workspaces)</option>"
                        : ">Workspace-wide (all environments in routed workspaces)</option>", html);
                    Assert.Equal(scope == AnalysisQueryScopes.Workspace, html.Contains(culture == "de-DE"
                        ? "Workspaceweit: Die ausgewählten Systeme bestimmen nur die Workspaces. Die Abfrage kann Daten aller Systeme/Domänen in diesen Workspaces zurückgeben. Aktivierte Workspaces, Validierung und Rollenanforderungen gelten weiterhin. _TimePeriod verwenden; _SelectedSystems ist optional."
                        : "Workspace-wide: selected systems choose the workspaces only. The query can return data from all systems/domains in those workspaces. Enabled workspaces, validation and role requirements still apply. Use _TimePeriod; _SelectedSystems is optional."));
                    foreach (var code in AnalysisQueryScopes.All.Concat(AnalysisQueryVisualizationTypes.All)
                        .Concat([SqlAuthenticationRoles.Operator, SqlAuthenticationRoles.Admin]))
                        Assert.Contains($"value=\"{code}\"", html);
                    Assert.Contains($"<option value=\"{scope}\" selected>", html);
                    Assert.Contains($"<option value=\"{role}\" selected>{role}</option>", html);
                    Assert.Contains($"<option value=\"{editor.VisualizationType}\" selected>{editor.VisualizationType}</option>", html);
                    Assert.DoesNotContain("value=\"Reader\"", html);
                    Assert.Contains($"value=\"{editor.Unit}\"", html);
                    Assert.Contains("Original &lt;description&gt; &amp;", rawEditor);
                    Assert.Contains(editor.VisualizationJson, html);
                    Assert.Contains(editor.Kql, html);
                    Assert.Contains("Original &lt;workspace&gt; &amp; (original-workspace)", rawEditor);
                    Assert.Contains("maxlength=\"128\"", rawEditor);
                    Assert.Contains("maxlength=\"256\"", rawEditor);
                    Assert.Contains("maxlength=\"64\"", rawEditor);
                    Assert.Contains("maxlength=\"2000\"", rawEditor);
                    Assert.Contains("maxlength=\"65536\"", rawEditor);
                    var numbers = Regex.Matches(rawEditor, "<input type=\"number\"[^>]*>");
                    Assert.Equal(3, numbers.Count);
                    for (var index = 0; index < numbers.Count; index++)
                    {
                        Assert.Contains(index == 2 ? "min=\"1\" max=\"1440\"" : "min=\"5\" max=\"43200\"", numbers[index].Value);
                        Assert.Contains($"value=\"{new[] { editor.DefaultTimeRangeMinutes, editor.MaxTimeRangeMinutes, editor.DefaultBinMinutes }[index]}\"", numbers[index].Value);
                    }
                    Assert.Equal(busy, Regex.Match(rawEditor, "<fieldset class=\"analysis-query-fields\"[^>]*>").Value.Contains("disabled"));
                    Assert.Equal(busy, Regex.Match(rawEditor, "<button class=\"admin-primary\" type=\"submit\"[^>]*>").Value.Contains("disabled"));
                    Assert.Equal(before, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(editor)));
                }
            }
            typeof(AdminAnalysisQueries).GetMethod("BeginCreate", flags)!.Invoke(page, []);
            var testEditor = Assert.IsType<AnalysisQueryDefinition>(typeof(AdminAnalysisQueries).GetField("editing", flags)!.GetValue(page));
            var testSystems = Enumerable.Range(0, 101).Select(index => new ComputerInventoryEntry
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                Computer = $"Original <DC{index:D3}> &",
                Domain = "original.example"
            }).ToArray();
            SetField("inventory", testSystems);
            var systemKeys = Assert.IsType<HashSet<string>>(typeof(AdminAnalysisQueries).GetField("testSystemKeys", flags)!.GetValue(page));
            foreach (var busy in new[] { false, true })
            foreach (var live in new[] { false, true })
            foreach (var hasWorkspace in new[] { false, true })
            foreach (var selectedCount in new[] { 0, 1, 2, 100 })
            {
                if (!hasWorkspace && selectedCount != 0) continue;
                SetField("isBusy", busy);
                typeof(AdminAnalysisQueries).GetProperty("Options", flags)!.SetValue(page,
                    new CrossTenantLogAnalyticsOptions { Enabled = live, Workspaces = options.Workspaces });
                Assert.Single(testEditor.Workspaces).Enabled = hasWorkspace;
                typeof(AdminAnalysisQueries).GetMethod("WorkspaceAssignmentsChanged", flags)!.Invoke(page, []);
                systemKeys.Clear();
                foreach (var system in testSystems.Take(selectedCount))
                    typeof(AdminAnalysisQueries).GetMethod("ToggleTestSystem", flags)!.Invoke(page,
                        [$"{system.SubscriptionId}\u001f{system.Computer}", new ChangeEventArgs { Value = true }]);
                var raw = await RenderPage();
                var html = WebUtility.HtmlDecode(raw);
                var testArea = Regex.Match(raw, "<fieldset class=\"admin-field admin-field-wide analysis-query-test\">.*?</fieldset>", RegexOptions.Singleline).Value;
                Assert.Contains(culture == "de-DE" ? ">Aktuellen Entwurf testen</legend>" : ">Test current draft</legend>", html);
                Assert.Contains(culture == "de-DE"
                    ? "Den aktuellen Editorinhalt ohne Speichern oder Veröffentlichen ausführen. Dieser Test ersetzt nicht die Workspace-Validierung."
                    : "Run the current editor content without saving or publishing. This test does not replace workspace validation.", html);
                Assert.Equal(!live, html.Contains(culture == "de-DE"
                    ? "Live-Abfragen in Log Analytics sind deaktiviert. Vor dem Testen konfigurieren und aktivieren."
                    : "Live Log Analytics is disabled. Configure and enable it before testing."));
                Assert.Contains(culture == "de-DE" ? ">Test-Workspace</span>" : ">Test workspace</span>", html);
                Assert.Contains(culture == "de-DE" ? ">Einen Workspace auswählen</option>" : ">Select a workspace</option>", html);
                Assert.Contains(culture == "de-DE"
                    ? $"Systeme für _SelectedSystems ({selectedCount} ausgewählt)"
                    : $"Systems for _SelectedSystems ({selectedCount} selected)", html);
                Assert.Equal(!hasWorkspace, html.Contains(culture == "de-DE"
                    ? "Diesem Workspace sind keine Inventarsysteme zugeordnet." : "No inventory systems route to this workspace."));
                var option = Regex.Match(testArea, "<option value=\"original-workspace\"[^>]*>");
                Assert.Equal(hasWorkspace, option.Success);
                if (hasWorkspace) Assert.Contains("selected", option.Value);
                var systemInputs = Regex.Matches(testArea, "<input type=\"checkbox\"[^>]*>");
                Assert.Equal(hasWorkspace ? 101 : 0, systemInputs.Count);
                Assert.Equal(selectedCount, systemInputs.Count(input => input.Value.Contains("checked")));
                Assert.Equal(selectedCount == 100 ? 1 : 0, systemInputs.Count(input => input.Value.Contains("disabled")));
                Assert.All(systemInputs.Where(input => input.Value.Contains("checked")),
                    input => Assert.DoesNotContain("disabled", input.Value));
                var caption = busy ? culture == "de-DE" ? "Wird bearbeitet..." : "Working..."
                    : culture == "de-DE" ? "Abfrage testen" : "Test query";
                var button = Regex.Match(html, $"<button class=\"admin-secondary\" type=\"button\"(?<attributes>[^>]*)>{Regex.Escape(caption)}</button>");
                Assert.True(button.Success);
                Assert.Equal(!live || busy || !hasWorkspace || selectedCount == 0, button.Groups["attributes"].Value.Contains("disabled"));
                Assert.Equal(selectedCount, systemKeys.Count);
                if (hasWorkspace) Assert.Contains("Original &lt;DC000&gt; &amp;", raw);
            }
            SetField("inventory", Array.Empty<ComputerInventoryEntry>());
            systemKeys.Clear();
            SetField("isBusy", false);
            Assert.Single(testEditor.Workspaces).Enabled = true;
            typeof(AdminAnalysisQueries).GetProperty("Options", flags)!.SetValue(page, options);
            typeof(AdminAnalysisQueries).GetMethod("WorkspaceAssignmentsChanged", flags)!.Invoke(page, []);
            var helpHtml = WebUtility.HtmlDecode(await RenderPage());
            Assert.Contains(culture == "de-DE"
                ? "Optionale Einstellungen als Objekt ({} behält Standardwerte bei): color, minimum/maximum, decimals, cellDisplay (Text, ColorText, ColorBackground), wrapText, thresholds, valueMappings und columns. Für Stat: statTitleFontSize und statValueFontSize (8-72 Pixel). Farben: green, red, yellow, orange, blue, purple, gray, teal oder #RRGGBB. Schwellenwerte müssen aufsteigend sein. Einstellungen werden zusammen mit der Abfrage validiert und veröffentlicht."
                : "Optional settings as an object ({} keeps defaults): color, minimum/maximum, decimals, cellDisplay (Text, ColorText, ColorBackground), wrapText, thresholds, valueMappings and columns. For Stat: statTitleFontSize and statValueFontSize (8-72 pixels). Colors: green, red, yellow, orange, blue, purple, gray, teal or #RRGGBB. Thresholds must be increasing. Settings are validated and published with the query.", helpHtml);
            Assert.DoesNotContain("{{}}", helpHtml);
            Assert.Contains(culture == "de-DE"
                ? "Systembezogene Abfragen müssen _SelectedSystems verwenden. Workspaceweite Abfragen müssen _TimePeriod verwenden und benötigen keine Computer-Spalte. Liniendiagramme/Statusverläufe verwenden _BinSize und geben Timestamp, Series, Value zurück (optional Label/Dimension). Waben/Balken/Messanzeigen geben Series, Value zurück. Statistiken geben Series und einen numerischen oder null-Wert als Value zurück (optional Dimension/Unit). Tabellen behalten alle Spalten bei; für Symbole ColumnName__Status (OK/Warning/Error/Unknown) hinzufügen. Visualisierungsspalten entsprechen den Tabellenspaltennamen oder Dimension/Series für Diagramme/Statistiken."
                : "System-scoped queries must use _SelectedSystems. Workspace-wide queries must use _TimePeriod and do not require a Computer column. Lines/status histories use _BinSize and return Timestamp, Series, Value (optional Label/Dimension). Honeycombs/bars/gauges return Series, Value. Statistics return Series, numeric or null Value (optional Dimension/Unit). Tables keep all columns; add ColumnName__Status (OK/Warning/Error/Unknown) for icons. Visualization columns match table column names, or Dimension/Series for charts/statistics.", helpHtml);
            Assert.Contains(culture == "de-DE" ? "Diesem Workspace sind keine Inventarsysteme zugeordnet." : "No inventory systems route to this workspace.", helpHtml);
            var preview = new AnalysisQueryExecutionPreview("original-workspace", "Original <preview> &",
                ["Original <B> &", "Original <A> &"], "// Original <KQL> &\r\nprint Value=1234");
            var from = new DateTimeOffset(2026, 11, 6, 16, 5, 9, TimeSpan.FromHours(2));
            var response = new AnalysisQueryResponse(Guid.NewGuid(), "Original <result> &", 3, "Percent",
                from, from.AddMinutes(37).AddSeconds(2), from.AddHours(1), 1234567, [], [],
                AnalysisQueryVisualizationTypes.Table,
                [new("Original <table> &", [new("Original <column> &", "string")], [new string?[] { "Original <value> &" }])]);
            var originalPreview = JsonSerializer.Serialize(preview);
            var originalResponse = JsonSerializer.Serialize(response);
            foreach (var hasPreview in new[] { false, true })
            foreach (var hasResult in new[] { false, true })
            foreach (var hasError in new[] { false, true })
            {
                SetField("testPreview", hasPreview ? preview : null);
                SetField("testResult", hasResult ? response : null);
                SetField("testError", hasError ? "Original <error> &" : null);
                var raw = await RenderPage();
                var output = Regex.Match(raw, "<section class=\"admin-analysis-test-output\".*?</section>", RegexOptions.Singleline).Value;
                var html = WebUtility.HtmlDecode(output);
                Assert.Contains(culture == "de-DE" ? ">Vorschau</p>" : ">Preview</p>", html);
                Assert.Contains(culture == "de-DE" ? ">Testausführung</h2>" : ">Test execution</h2>", html);
                Assert.Equal(hasError, output.Contains("Original &lt;error&gt; &amp;"));
                Assert.Equal(hasError, output.Contains("role=\"alert\""));
                Assert.Equal(!hasPreview, html.Contains(culture == "de-DE"
                    ? "Im Entwurf einen Workspace und Systeme auswählen und dann „Abfrage testen“ ausführen, um hier die effektive KQL und Ergebnisse zu sehen."
                    : "Select a workspace and systems in the draft, then run Test query to see the effective KQL and results here."));
                Assert.Equal(hasPreview && hasResult, html.Contains(culture == "de-DE"
                    ? "2026-11-06 14:05:09Z bis 2026-11-06 14:42:11Z · 1234567 ms"
                    : "2026-11-06 14:05:09Z to 2026-11-06 14:42:11Z · 1234567 ms"));
                Assert.Equal(hasPreview && hasResult, output.Contains("Original &lt;value&gt; &amp;"));
                if (hasPreview)
                {
                    Assert.Contains("Original &lt;preview&gt; &amp;", output);
                    Assert.Contains(culture == "de-DE" ? "Systeme: Original <B> &, Original <A> &" : "Systems: Original <B> &, Original <A> &", html);
                    Assert.Contains(culture == "de-DE" ? ">Für diesen Test verwendete effektive KQL</summary>" : ">Effective KQL used for this test</summary>", html);
                    Assert.Contains(preview.EffectiveKql, html);
                    Assert.Contains("Original &lt;KQL&gt; &amp;", output);
                }
                Assert.Equal(originalPreview, JsonSerializer.Serialize(preview));
                Assert.Equal(originalResponse, JsonSerializer.Serialize(response));
            }
            SetField("testPreview", null);
            SetField("testResult", null);
            SetField("testError", null);
            SetField("editing", null);
            SetField("isBusy", false);
            SetField("bulkIncludeDisabledQueries", true);
            selectedWorkspaces.Clear();
            typeof(AdminAnalysisQueries).GetMethod("BeginCategoryCreate", flags)!.Invoke(page, [""]);
            foreach (var busy in new[] { false, true })
            {
                SetField("isBusy", busy);
                SetField("queryToUnpublish", new AnalysisQueryDefinition { Name = "Original <withdraw> &" });
                SetField("statusMessage", "Original <status> &");
                SetField("errorMessage", "Original <failure> &");
                var raw = await RenderPage();
                var dialog = Regex.Match(raw, "<section class=\"admin-message admin-message-warning\" role=\"alertdialog\".*?</section>", RegexOptions.Singleline).Value;
                var html = WebUtility.HtmlDecode(dialog);
                Assert.Contains("aria-labelledby=\"unpublish-query-heading\"", dialog);
                Assert.Contains(culture == "de-DE"
                    ? ">Veröffentlichung der Abfrage für alle zurückziehen?</h2>" : ">Unpublish query for everyone?</h2>", html);
                Assert.Contains("<strong>Original &lt;withdraw&gt; &amp;</strong>", dialog);
                Assert.Contains(culture == "de-DE"
                    ? "<strong>Original <withdraw> &</strong> steht nicht mehr zur Ausführung zur Verfügung. Vorhandene Widgets behalten ihre Konfiguration, können diese Abfrage jedoch erst nach einer erneuten Veröffentlichung ausführen. Der Entwurf und alle bisherigen Versionen bleiben erhalten."
                    : "<strong>Original <withdraw> &</strong> will no longer be available for execution. Existing widgets keep their configuration but cannot run this query until it is published again. The draft and all previous versions are retained.", html);
                Assert.Contains(culture == "de-DE" ? ">Abbrechen</button>" : ">Cancel</button>", html);
                Assert.Contains(culture == "de-DE" ? ">Für alle zurückziehen</button>" : ">Unpublish for everyone</button>", html);
                var buttons = Regex.Matches(dialog, "<button\\b[^>]*>");
                Assert.Equal(2, buttons.Count);
                Assert.All(buttons.Cast<Match>(), button => Assert.Equal(busy, button.Value.Contains("disabled")));
                Assert.Contains("Original &lt;status&gt; &amp;", raw);
                Assert.Contains("Original &lt;failure&gt; &amp;", raw);
            }
            SetField("queryToUnpublish", null);
            SetField("statusMessage", null);
            SetField("errorMessage", null);
            SetField("isBusy", false);
            Assert.DoesNotContain("role=\"alertdialog\"", await RenderPage());
            Assert.Equal(original, await Snapshot());
            Assert.Equal(originalCategories, await categories.ListAsync());
            await using var verify = factory.CreateDbContext();
            Assert.Equal(count, await verify.AnalysisQueryDefinitions.CountAsync());
            Assert.Equal(0, await verify.AnalysisQueryVersions.CountAsync());
            Assert.Equal(count, await verify.AnalysisQueryWorkspaces.CountAsync(workspace => workspace.WorkspaceKey == "original-workspace"));
            Assert.Empty(log.Errors);
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            Assert.Equal("AdminAccess", Assert.Single(typeof(AdminAnalysisQueries).GetCustomAttributes<AuthorizeAttribute>()).Policy);
            Assert.Equal("/admin/analysis-queries", Assert.Single(typeof(AdminAnalysisQueries).GetCustomAttributes<RouteAttribute>()).Template);
            if (count == 0)
            {
                async Task RunAndAssert(string method, string english, string german, params object?[] arguments)
                {
                    SetField("errorMessage", "Previous <failure> &");
                    await renderer.Dispatcher.InvokeAsync(() => (Task)typeof(AdminAnalysisQueries).GetMethod(method, flags)!.Invoke(page, arguments)!);
                    Assert.Null(typeof(AdminAnalysisQueries).GetField("errorMessage", flags)!.GetValue(page));
                    Assert.False(Assert.IsType<bool>(typeof(AdminAnalysisQueries).GetField("isBusy", flags)!.GetValue(page)));
                    var message = culture == "de-DE" ? german : english;
                    Assert.Equal(message, typeof(AdminAnalysisQueries).GetField("statusMessage", flags)!.GetValue(page));
                    Assert.Contains($"role=\"status\">{message}</div>", WebUtility.HtmlDecode(await RenderPage()));
                }
                const string lifecycleCategory = "Lifecycle <category> &";
                SetField("categoryName", lifecycleCategory);
                await RunAndAssert("SaveCategoryAsync",
                    "Category saved. Library organization is updated without publishing a new query version.",
                    "Kategorie gespeichert. Die Bibliotheksstruktur wird ohne Veröffentlichung einer neuen Abfrageversion aktualisiert.");
                await RunAndAssert("DeleteCategoryAsync", "Empty category deleted.", "Leere Kategorie gelöscht.", lifecycleCategory);
                await categories.CreateAsync(lifecycleCategory, "");
                var template = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-fsmo-roles");
                template.Key = "lifecycle-original";
                template.Name = "Original <lifecycle> &";
                var json = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(template), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await RunAndAssert("ImportJsonAsync",
                    "JSON opened as a new, unsaved draft. Save the draft separately; validation and publication remain separate steps.",
                    "JSON als neuen, nicht gespeicherten Entwurf geöffnet. Den Entwurf separat speichern; Validierung und Veröffentlichung bleiben getrennte Schritte.", json);
                Assert.Empty(await registry.ListAsync());
                await RunAndAssert("SaveAsync",
                    "Draft saved. Workspace validation must be rerun before publishing.",
                    "Entwurf gespeichert. Die Workspace-Validierung muss vor der Veröffentlichung erneut ausgeführt werden.");
                var saved = Assert.Single(await registry.ListAsync());
                await RunAndAssert("MoveQueryAsync",
                    "Query moved. Refresh the library to see the new category; no republishing is needed.",
                    "Abfrage verschoben. Die Bibliothek aktualisieren, um die neue Kategorie zu sehen; eine erneute Veröffentlichung ist nicht erforderlich.",
                    saved.Id, lifecycleCategory);
                Assert.Equal(lifecycleCategory, Assert.Single(await registry.ListAsync()).Category);
                var client = new LocalQueryClient();
                var liveOptions = new CrossTenantLogAnalyticsOptions { Enabled = true, ApplicationClientId = "local-test", Workspaces = options.Workspaces };
                var localRunner = new AnalysisQueryRunner(liveOptions, new LocalSecretProvider(), new TargetWorkspaceResolver(liveOptions), client);
                var localRegistry = new AnalysisQueryRegistry(factory, liveOptions, localRunner);
                typeof(AdminAnalysisQueries).GetProperty("Options", flags)!.SetValue(page, liveOptions);
                typeof(AdminAnalysisQueries).GetProperty("Registry", flags)!.SetValue(page, localRegistry);
                typeof(AdminAnalysisQueries).GetProperty("QueryRunner", flags)!.SetValue(page, localRunner);
                var system = new ComputerInventoryEntry
                {
                    Computer = "Lifecycle-DC", Domain = "original.example",
                    SubscriptionId = "11111111-1111-1111-1111-111111111111"
                };
                await using (var context = factory.CreateDbContext())
                {
                    context.ComputerInventory.Add(system);
                    await context.SaveChangesAsync();
                }
                SetField("inventory", new[] { system });
                SetField("testWorkspaceKey", "original-workspace");
                systemKeys.Add($"{system.SubscriptionId}\u001f{system.Computer}");
                await RunAndAssert("TestAsync",
                    "Draft test completed. Save, validate, and publish separately to make this query available to operators.",
                    "Entwurfstest abgeschlossen. Separat speichern, validieren und veröffentlichen, um diese Abfrage für Operatoren verfügbar zu machen.");
                Assert.Single(await registry.ListAsync());
                Assert.Empty(Assert.Single(await registry.ListAsync()).Versions);
                await RunAndAssert("ValidateAsync", "Workspace validation completed.", "Workspace-Validierung abgeschlossen.", saved.Id);
                Assert.Equal(AnalysisQueryStatuses.Validated, Assert.Single(await registry.ListAsync()).Status);
                Assert.Equal(AnalysisQueryValidationStatuses.Succeeded, Assert.Single(Assert.Single(await registry.ListAsync()).Workspaces).LastValidationStatus);
                await RunAndAssert("PublishAsync", "Query version published.", "Abfrageversion veröffentlicht.", saved.Id);
                var published = Assert.Single(await registry.ListAsync());
                Assert.NotNull(published.PublishedVersionId);
                var version = Assert.Single(published.Versions).SnapshotJson;
                SetField("queryToUnpublish", published);
                await RunAndAssert("UnpublishAsync",
                    "Publication withdrawn for everyone. The draft and version history were retained. Validate and publish to release it again.",
                    "Veröffentlichung für alle zurückgezogen. Der Entwurf und der Versionsverlauf bleiben erhalten. Zum erneuten Freigeben validieren und veröffentlichen.");
                Assert.Null(typeof(AdminAnalysisQueries).GetField("queryToUnpublish", flags)!.GetValue(page));
                var withdrawn = Assert.Single(await registry.ListAsync());
                Assert.Null(withdrawn.PublishedVersionId);
                Assert.Equal(version, Assert.Single(withdrawn.Versions).SnapshotJson);
                Assert.Equal(saved.Kql, withdrawn.Kql);
                await RunAndAssert("DisableAsync", "Query disabled.", "Abfrage deaktiviert.", saved.Id);
                Assert.False(Assert.Single(await registry.ListAsync()).Enabled);
                var second = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-fsmo-roles");
                second.Key = "lifecycle-second";
                second.Workspaces.Add(new() { WorkspaceKey = "original-workspace" });
                var secondSaved = await registry.SaveDraftAsync(second, "admin");
                await using (var context = factory.CreateDbContext())
                {
                    var assignment = await context.AnalysisQueryWorkspaces.SingleAsync(item => item.QueryDefinitionId == secondSaved.Id);
                    assignment.Enabled = false;
                    await context.SaveChangesAsync();
                }
                SetField("editing", null);
                selectedWorkspaces.Add("original-workspace");
                await RunAndAssert("AssignWorkspacesToAllAsync",
                    "Updated 2 query/queries: enabled 1 workspace assignment(s) and re-enabled 1 disabled query/queries. New workspace assignments require validation; no new query versions were published.",
                    "Aktualisierte Abfragen: 2; aktivierte Workspace-Zuordnungen: 1; erneut aktivierte deaktivierte Abfragen: 1. Neue Workspace-Zuordnungen erfordern eine Validierung; es wurden keine neuen Abfrageversionen veröffentlicht.");
                await RunAndAssert("AssignWorkspacesToAllAsync",
                    "Updated 0 query/queries: enabled 0 workspace assignment(s) and re-enabled 0 disabled query/queries. New workspace assignments require validation; no new query versions were published.",
                    "Aktualisierte Abfragen: 0; aktivierte Workspace-Zuordnungen: 0; erneut aktivierte deaktivierte Abfragen: 0. Neue Workspace-Zuordnungen erfordern eine Validierung; es wurden keine neuen Abfrageversionen veröffentlicht.");
                var beforeLibrary = (await registry.ListAsync()).OrderBy(query => query.Key)
                    .Select(query => JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))).ToArray();
                foreach (var libraryTemplate in ActiveDirectoryAnalysisQueries.Create().Skip(1))
                {
                    libraryTemplate.Workspaces.Add(new() { WorkspaceKey = "original-workspace" });
                    await registry.SaveDraftAsync(libraryTemplate, "admin");
                }
                await RunAndAssert("InstallAdLibraryAsync",
                    "Added 1 AD query draft(s); existing definitions were preserved. Select workspaces, test, validate and publish before use.",
                    "1 AD-Abfrageentwurf hinzugefügt; vorhandene Definitionen bleiben erhalten. Vor der Verwendung Workspaces auswählen, testen, validieren und veröffentlichen.");
                await RunAndAssert("InstallAdLibraryAsync",
                    "Added 0 AD query draft(s); existing definitions were preserved. Select workspaces, test, validate and publish before use.",
                    "0 AD-Abfrageentwürfe hinzugefügt; vorhandene Definitionen bleiben erhalten. Vor der Verwendung Workspaces auswählen, testen, validieren und veröffentlichen.");
                var after = await registry.ListAsync();
                Assert.Equal(40, after.Count);
                Assert.Equal(beforeLibrary, after.Where(query => query.Key.StartsWith("lifecycle-", StringComparison.Ordinal))
                    .OrderBy(query => query.Key).Select(query => JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))));
                Assert.Equal(version, Assert.Single(after.Single(query => query.Id == saved.Id).Versions).SnapshotJson);
                Assert.Equal(2, client.Calls);
                Assert.Empty(log.Errors);
                Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = oldUiCulture;
            CultureInfo.CurrentCulture = oldCulture;
        }
    }

    [Theory]
    [MemberData(nameof(QueryJsonImportTests.InvalidImportCases), MemberType = typeof(QueryJsonImportTests))]
    public async Task AnalysisImportBoundariesPreserveEditorAndTestState(string culture, string input, QueryTemplateValidationCode code)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        fixture.Set("editing", fixture.Query);
        fixture.Set("testWorkspaceKey", "original-workspace");
        Assert.IsType<HashSet<string>>(fixture.Field("testSystemKeys")).Add(
            fixture.System.SubscriptionId + "\u001f" + fixture.System.Computer);
        await fixture.InvokeAsync("TestAsync");
        Assert.NotNull(fixture.Field("testPreview"));
        var preview = fixture.Field("testPreview");
        var result = fixture.Field("testResult");
        fixture.Set("testError", "Original test diagnostic");
        fixture.Query.Kql = "Original unsaved KQL";
        var snapshot = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync())));
        var writes = fixture.Writes.Attempts;
        await fixture.InvokeAsync("ImportJsonAsync", QueryJsonImportTests.InvalidImportJson(input));
        var exception = Assert.IsType<QueryTemplateValidationException>(
            Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        Assert.Equal(code, exception.Code);
        var expected = fixture.Text[$"QueryTemplate.Validation.{code}"];
        Assert.Equal(expected, fixture.Field("errorMessage"));
        var html = await fixture.HtmlAsync();
        Assert.Contains(expected, html);
        Assert.DoesNotContain("private JSON diagnostic", html);
        Assert.Same(fixture.Query, fixture.Field("editing"));
        Assert.Equal("Original unsaved KQL", fixture.Query.Kql);
        Assert.Same(preview, fixture.Field("testPreview"));
        Assert.Same(result, fixture.Field("testResult"));
        Assert.Equal("Original test diagnostic", fixture.Field("testError"));
        Assert.Null(fixture.Field("statusMessage"));
        Assert.Null(fixture.Field("loadError"));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal(snapshot, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync()))));
        Assert.All(fixture.Log.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        if (code == QueryTemplateValidationCode.ImportTooLarge)
        {
            var json = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync())),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            json += new string(' ', QueryTemplateJson.MaxImportBytes - System.Text.Encoding.UTF8.GetByteCount(json));
            Assert.Equal(65536, System.Text.Encoding.UTF8.GetByteCount(json));
            await fixture.InvokeAsync("ImportJsonAsync", json);
            Assert.Null(fixture.Field("errorMessage"));
            Assert.Null(fixture.Field("testPreview"));
            Assert.Null(fixture.Field("testResult"));
            Assert.Null(fixture.Field("testError"));
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Status.ImportOpened"], fixture.Field("statusMessage"));
            var imported = Assert.IsType<AnalysisQueryDefinition>(fixture.Field("editing"));
            Assert.NotSame(fixture.Query, imported);
            Assert.Equal(Guid.Empty, imported.Id);
            Assert.Equal(AnalysisQueryStatuses.Draft, imported.Status);
            Assert.Empty(imported.Versions);
            Assert.Equal(writes, fixture.Writes.Attempts);
            Assert.Equal(snapshot, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync()))));
        }
    }

    [Theory]
    [InlineData("en-US", "ValidateAsync")]
    [InlineData("de-DE", "ValidateAsync")]
    [InlineData("en-US", "PublishAsync")]
    [InlineData("de-DE", "PublishAsync")]
    [InlineData("en-US", "UnpublishAsync")]
    [InlineData("de-DE", "UnpublishAsync")]
    [InlineData("en-US", "DisableAsync")]
    [InlineData("de-DE", "DisableAsync")]
    public async Task MissingRegistryTargetsPreserveTheEditorAndNeverWriteOrExecute(string culture, string method)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        fixture.Set("editing", fixture.Query);
        fixture.Query.Kql = "Original unsaved KQL";
        fixture.Set("queryToUnpublish", fixture.Query);
        var cachedQueries = fixture.Field("queries");
        await using (var context = fixture.Factory.CreateDbContext())
            await context.AnalysisQueryDefinitions.Where(query => query.Id == fixture.Query.Id).ExecuteDeleteAsync();
        var writes = fixture.Writes.Attempts;
        if (method == "UnpublishAsync") await fixture.InvokeAsync(method);
        else await fixture.InvokeAsync(method, fixture.Query.Id);
        var exception = Assert.IsType<AnalysisValidationException>(
            Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        Assert.Equal(AnalysisValidationCode.RegistryQueryMissing, exception.Code);
        Assert.Equal("The query no longer exists. Refresh the registry.", exception.Message);
        var expected = fixture.Text["Analysis.Validation.RegistryQueryMissing"];
        Assert.Equal(expected, fixture.Field("errorMessage"));
        Assert.Contains(expected, await fixture.HtmlAsync());
        Assert.Same(fixture.Query, fixture.Field("editing"));
        Assert.Equal("Original unsaved KQL", fixture.Query.Kql);
        Assert.Same(fixture.Query, fixture.Field("queryToUnpublish"));
        Assert.Same(cachedQueries, fixture.Field("queries"));
        Assert.Null(fixture.Field("statusMessage"));
        Assert.Null(fixture.Field("loadError"));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal(0, fixture.Client.Calls);
        Assert.Empty(await fixture.Registry.ListAsync());
        Assert.All(fixture.Log.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        await fixture.InvokeAsync("RefreshAsync");
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<AnalysisQueryDefinition>>(fixture.Field("queries")));
        Assert.Same(fixture.Query, fixture.Field("editing"));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftSaveStillUpsertsExplicitIdsWithoutResurrectingPublication(bool deleted)
    {
        await using var fixture = await BoundaryFixture.CreateAsync();
        var draft = fixture.Query;
        if (deleted)
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.AnalysisQueryDefinitions.Where(query => query.Id == draft.Id).ExecuteDeleteAsync();
        }
        else
        {
            draft.Id = Guid.NewGuid();
            draft.Key = "explicit-upsert";
        }
        draft.Version = 42;
        draft.Status = AnalysisQueryStatuses.Published;
        draft.PublishedVersionId = Guid.NewGuid();
        var saved = await fixture.Registry.SaveDraftAsync(draft, "upsert-author");
        Assert.Equal(draft.Id, saved.Id);
        Assert.Equal(AnalysisQueryStatuses.Draft, saved.Status);
        Assert.Equal(0, saved.Version);
        Assert.Null(saved.PublishedVersionId);
        Assert.Empty(saved.Versions);
        Assert.Equal("upsert-author", saved.UpdatedBy);
        Assert.Equal(0, fixture.Client.Calls);
    }

    public static IEnumerable<object[]> CategoryValidationCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var code in new[]
        {
            AnalysisValidationCode.CategoryRequired, AnalysisValidationCode.CategoryPathInvalid,
            AnalysisValidationCode.CategoryNameInvalid, AnalysisValidationCode.CategoryExists,
            AnalysisValidationCode.CategoryMissing, AnalysisValidationCode.CategoryCycle,
            AnalysisValidationCode.CategoryCollision, AnalysisValidationCode.CategoryNotEmpty,
            AnalysisValidationCode.CategoryQueryMissing
        })
            yield return [culture, code];
    }

    [Theory]
    [MemberData(nameof(CategoryValidationCases))]
    public async Task CategoryValidationCodesPreserveDraftsLibraryAndAudit(string culture, AnalysisValidationCode code)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.Categories.CreateAsync("AP6", null);
        await fixture.Categories.CreateAsync("Child", "AP6");
        await fixture.Categories.CreateAsync("Other", null);
        await fixture.StartAsync();
        var paths = (await fixture.Categories.ListAsync()).ToArray();
        var before = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync())));
        var draft = fixture.Query;
        draft.Kql = "Original unsaved <KQL> &";
        fixture.Set("editing", draft);
        fixture.Set("categoryName", "Renamed");
        switch (code)
        {
            case AnalysisValidationCode.CategoryRequired: fixture.Set("categoryName", ""); break;
            case AnalysisValidationCode.CategoryPathInvalid: fixture.Set("categoryName", new string('x', 129)); break;
            case AnalysisValidationCode.CategoryNameInvalid: fixture.Set("categoryName", "Invalid/Name"); break;
            case AnalysisValidationCode.CategoryExists: fixture.Set("categoryName", "ap6"); break;
            case AnalysisValidationCode.CategoryMissing: fixture.Set("categoryToEdit", "Missing"); break;
            case AnalysisValidationCode.CategoryCycle:
                fixture.Set("categoryToEdit", "AP6");
                fixture.Set("categoryParent", "AP6/Child");
                break;
            case AnalysisValidationCode.CategoryCollision:
                fixture.Set("categoryToEdit", "AP6");
                fixture.Set("categoryName", "Other");
                break;
            case AnalysisValidationCode.CategoryNotEmpty:
            case AnalysisValidationCode.CategoryQueryMissing: break;
            default: throw new ArgumentOutOfRangeException(nameof(code));
        }
        var categoryName = fixture.Field("categoryName");
        var categoryParent = fixture.Field("categoryParent");
        var categoryToEdit = fixture.Field("categoryToEdit");
        var writes = fixture.Writes.Attempts;
        if (code == AnalysisValidationCode.CategoryNotEmpty) await fixture.InvokeAsync("DeleteCategoryAsync", "AP6");
        else if (code == AnalysisValidationCode.CategoryQueryMissing) await fixture.InvokeAsync("MoveQueryAsync", Guid.NewGuid(), "AP6");
        else await fixture.InvokeAsync("SaveCategoryAsync");
        var exception = Assert.IsType<AnalysisValidationException>(
            Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        Assert.Equal(code, exception.Code);
        Assert.Equal(fixture.Text[$"Analysis.Validation.{code}"], fixture.Field("errorMessage"));
        Assert.Contains(fixture.Text[$"Analysis.Validation.{code}"], await fixture.HtmlAsync());
        Assert.Same(draft, fixture.Field("editing"));
        Assert.Equal("Original unsaved <KQL> &", draft.Kql);
        Assert.Equal(categoryName, fixture.Field("categoryName"));
        Assert.Equal(categoryParent, fixture.Field("categoryParent"));
        Assert.Equal(categoryToEdit, fixture.Field("categoryToEdit"));
        Assert.Null(fixture.Field("statusMessage"));
        Assert.Null(fixture.Field("loadError"));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal(paths, await fixture.Categories.ListAsync());
        Assert.Equal(before, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync()))));
        Assert.All(fixture.Log.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    public static IEnumerable<object[]> RegistryValidationCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var action in new[] { "install", "save-empty", "save-unavailable", "bulk-empty", "bulk-unavailable",
            "duplicate", "publish", "withdraw", "systems" })
            yield return [culture, action];
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task UnavailableWorkspaceDraftRecoveryIsExplicitAndDoesNotWrite(string culture)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        var assignment = Assert.Single(fixture.Query.Workspaces);
        assignment.WorkspaceKey = "ORIGINAL-WORKSPACE";
        fixture.Set("editing", fixture.Query);
        var writes = fixture.Writes.Attempts;
        await fixture.InvokeAsync("RemoveUnavailableWorkspace", assignment);
        Assert.Same(assignment, Assert.Single(fixture.Query.Workspaces));
        Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.Changed"], fixture.Field("errorMessage"));
        Assert.DoesNotContain(fixture.Text["AdminAnalysisQueries.RemoveUnavailableWorkspace"], await fixture.HtmlAsync());
        assignment.WorkspaceKey = "Missing <workspace> &";
        fixture.Set("isBusy", true);
        await fixture.InvokeAsync("RemoveUnavailableWorkspace", assignment);
        Assert.Single(fixture.Query.Workspaces);
        fixture.Set("isBusy", false);
        fixture.Set("loadError", "Original load error");
        await fixture.InvokeAsync("RemoveUnavailableWorkspace", assignment);
        Assert.Single(fixture.Query.Workspaces);
        fixture.Set("loadError", null);
        await fixture.InvokeAsync("SaveAsync");
        var html = await fixture.HtmlAsync();
        Assert.Contains(fixture.Text.Format("AdminAnalysisQueries.UnavailableWorkspace", assignment.WorkspaceKey), html);
        Assert.Contains(fixture.Text["Analysis.Validation.RegistryWorkspaceUnavailable"], html);
        await fixture.InvokeAsync("RemoveUnavailableWorkspace", assignment);
        Assert.Empty(fixture.Query.Workspaces);
        Assert.Null(fixture.Field("errorMessage"));
        Assert.Same(fixture.Query, fixture.Field("editing"));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal("original-workspace", Assert.Single(Assert.Single(await fixture.Registry.ListAsync()).Workspaces).WorkspaceKey);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [MemberData(nameof(RegistryValidationCases))]
    public async Task RegistryValidationErrorsAreLocalizedWithoutMutatingStoredQueries(string culture, string action)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        var original = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync())));
        var writes = fixture.Writes.Attempts;
        AnalysisValidationCode code;
        switch (action)
        {
            case "install":
                var options = new CrossTenantLogAnalyticsOptions();
                var runner = new AnalysisQueryRunner(options, new LocalSecretProvider(), new TargetWorkspaceResolver(options), fixture.Client);
                typeof(AdminAnalysisQueries).GetProperty("Registry", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(fixture.Page, new AnalysisQueryRegistry(fixture.Factory, options, runner));
                await fixture.InvokeAsync("InstallAdLibraryAsync");
                code = AnalysisValidationCode.RegistryWorkspaceRequired;
                break;
            case "save-empty":
            case "save-unavailable":
            case "duplicate":
                if (action == "save-empty") fixture.Query.Workspaces.Clear();
                else if (action == "save-unavailable") Assert.Single(fixture.Query.Workspaces).WorkspaceKey = "missing-workspace";
                else fixture.Query.Id = Guid.NewGuid();
                fixture.Set("editing", fixture.Query);
                await fixture.InvokeAsync("SaveAsync");
                code = action switch
                {
                    "save-empty" => AnalysisValidationCode.RegistryWorkspaceRequired,
                    "save-unavailable" => AnalysisValidationCode.RegistryWorkspaceUnavailable,
                    _ => AnalysisValidationCode.RegistryKeyExists
                };
                Assert.Same(fixture.Query, fixture.Field("editing"));
                break;
            case "bulk-empty":
            case "bulk-unavailable":
                if (action == "bulk-unavailable")
                    Assert.IsType<HashSet<string>>(fixture.Field("bulkWorkspaceKeys")).Add("missing-workspace");
                await fixture.InvokeAsync("AssignWorkspacesToAllAsync");
                code = action == "bulk-empty" ? AnalysisValidationCode.RegistryWorkspaceRequired : AnalysisValidationCode.RegistryWorkspaceUnavailable;
                break;
            case "publish":
                await fixture.InvokeAsync("PublishAsync", fixture.Query.Id);
                code = AnalysisValidationCode.RegistryPublishRequired;
                break;
            case "withdraw":
                fixture.Set("queryToUnpublish", fixture.Query);
                await fixture.InvokeAsync("UnpublishAsync");
                Assert.Same(fixture.Query, fixture.Field("queryToUnpublish"));
                code = AnalysisValidationCode.RegistryPublicationMissing;
                break;
            case "systems":
                Func<Task> test = () => fixture.Registry.TestDraftAsync(fixture.Query, "original-workspace", [], 60);
                await fixture.InvokeAsync("RunAsync", test, "AdminAnalysisQueries.Errors.TestFailed");
                code = AnalysisValidationCode.SystemsRequired;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        Assert.Equal(code, Assert.IsType<AnalysisValidationException>(
            Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception).Code);
        Assert.Equal(fixture.Text[$"Analysis.Validation.{code}"], fixture.Field("errorMessage"));
        Assert.Contains(fixture.Text[$"Analysis.Validation.{code}"], await fixture.HtmlAsync());
        Assert.Null(fixture.Field("statusMessage"));
        Assert.Null(fixture.Field("loadError"));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal(0, fixture.Client.Calls);
        Assert.Equal(original, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync()))));
        Assert.All(fixture.Log.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    public static IEnumerable<object[]> QuerySchemaCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var code in Enum.GetValues<AnalysisQueryValidationCode>())
            yield return [culture, code];
    }

    [Theory]
    [MemberData(nameof(QuerySchemaCases))]
    public async Task SharedQuerySchemaIssuesReachSaveAndTestWithoutLosingDrafts(string culture, AnalysisQueryValidationCode code)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        var storedBefore = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync())));
        var draft = fixture.Query;
        switch (code)
        {
            case AnalysisQueryValidationCode.KeyInvalid: draft.Key = ""; break;
            case AnalysisQueryValidationCode.NameInvalid: draft.Name = ""; break;
            case AnalysisQueryValidationCode.CategoryInvalid: draft.Category = "Invalid//path"; break;
            case AnalysisQueryValidationCode.DefaultTimeRangeInvalid: draft.DefaultTimeRangeMinutes = 4; break;
            case AnalysisQueryValidationCode.MaxTimeRangeInvalid: draft.MaxTimeRangeMinutes = draft.DefaultTimeRangeMinutes - 1; break;
            case AnalysisQueryValidationCode.BinInvalid: draft.DefaultBinMinutes = 0; break;
            case AnalysisQueryValidationCode.VisualizationTypeInvalid: draft.VisualizationType = "unsupported"; break;
            case AnalysisQueryValidationCode.ScopeInvalid: draft.Scope = "unsupported"; break;
            case AnalysisQueryValidationCode.MinimumRoleInvalid: draft.MinimumRole = SqlAuthenticationRoles.Reader; break;
            case AnalysisQueryValidationCode.VisualizationOptionsInvalid: draft.VisualizationJson = "{"; break;
            case AnalysisQueryValidationCode.KqlRequired: draft.Kql = ""; break;
            case AnalysisQueryValidationCode.SelectedSystemsRequired: draft.Kql = "Heartbeat"; break;
            case AnalysisQueryValidationCode.WorkspaceTimeRequired:
                draft.Scope = AnalysisQueryScopes.Workspace;
                draft.VisualizationType = AnalysisQueryVisualizationTypes.Table;
                draft.Kql = "Heartbeat";
                break;
            case AnalysisQueryValidationCode.BinParameterRequired:
                draft.VisualizationType = AnalysisQueryVisualizationTypes.LineChart;
                draft.Kql = "Heartbeat | where Computer in~ (_SelectedSystems)";
                break;
            case AnalysisQueryValidationCode.UnsafeKql:
                draft.Kql = "workspace('external').Heartbeat | where Computer in~ (_SelectedSystems)";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(code));
        }
        fixture.Set("editing", draft);
        var writes = fixture.Writes.Attempts;
        var kqlIssue = code is AnalysisQueryValidationCode.KqlRequired or AnalysisQueryValidationCode.SelectedSystemsRequired
            or AnalysisQueryValidationCode.WorkspaceTimeRequired or AnalysisQueryValidationCode.BinParameterRequired or AnalysisQueryValidationCode.UnsafeKql;
        var legacyMessages = AnalysisQueryValidator.ValidateDefinition(draft).ToList();
        if (kqlIssue)
        {
            legacyMessages.AddRange(AnalysisQueryValidator.ValidateKql(draft.Kql, draft.VisualizationType, draft.Scope));
            fixture.Set("testWorkspaceKey", "original-workspace");
            Assert.IsType<HashSet<string>>(fixture.Field("testSystemKeys")).Add(
                fixture.System.SubscriptionId + "\u001f" + fixture.System.Computer);
        }
        await fixture.InvokeAsync(kqlIssue ? "TestAsync" : "SaveAsync");
        var validation = Assert.IsType<AnalysisQueryValidationException>(
            Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        Assert.Contains(validation.Issues, issue => issue.Code == code);
        Assert.Equal(legacyMessages, validation.Issues.Select(issue => issue.Message));
        Assert.Equal(string.Join(" ", legacyMessages), validation.Message);
        var message = string.Join(" ", validation.Issues.Select(issue => issue.Parameter is null
            ? fixture.Text[$"AnalysisQuery.Validation.{issue.Code}"]
            : fixture.Text.Format($"AnalysisQuery.Validation.{issue.Code}", issue.Parameter)));
        Assert.Equal(message, fixture.Field("errorMessage"));
        Assert.Contains(message, await fixture.HtmlAsync());
        if (kqlIssue) Assert.Equal(message, fixture.Field("testError"));
        Assert.Same(draft, fixture.Field("editing"));
        Assert.Null(fixture.Field("statusMessage"));
        Assert.Null(fixture.Field("loadError"));
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal(0, fixture.Client.Calls);
        Assert.Equal(storedBefore, JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(Assert.Single(await fixture.Registry.ListAsync()))));
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MultipleSchemaFailuresPreserveOrderAndDraftOnlyKqlRemainsSaveable(string culture)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        fixture.Query.Key = "";
        fixture.Query.Name = "";
        fixture.Query.Category = "";
        fixture.Set("editing", fixture.Query);
        await fixture.InvokeAsync("SaveAsync");
        var exception = Assert.IsType<AnalysisQueryValidationException>(
            Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        Assert.Equal(new[] { AnalysisQueryValidationCode.KeyInvalid, AnalysisQueryValidationCode.NameInvalid, AnalysisQueryValidationCode.CategoryInvalid },
            exception.Issues.Select(issue => issue.Code));
        Assert.Equal(string.Join(" ", exception.Issues.Select(issue => fixture.Text[$"AnalysisQuery.Validation.{issue.Code}"])),
            fixture.Field("errorMessage"));
        fixture.Query.Key = "boundary-query";
        fixture.Query.Name = "Original custom query";
        fixture.Query.Category = "Custom category";
        fixture.Query.Kql = ".show tables";
        await fixture.InvokeAsync("SaveAsync");
        Assert.Null(fixture.Field("errorMessage"));
        Assert.Equal(".show tables", Assert.Single(await fixture.Registry.ListAsync()).Kql);
        var writes = fixture.Writes.Attempts;
        await fixture.InvokeAsync("ValidateAsync", fixture.Query.Id);
        Assert.Contains(fixture.Log.Entries, entry => entry.Exception is AnalysisQueryValidationException);
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Equal(AnalysisQueryStatuses.Draft, Assert.Single(await fixture.Registry.ListAsync()).Status);
    }

    public static IEnumerable<object[]> ReadFailureCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var source in new[] { 0, 1, 2 })
        foreach (var initial in new[] { false, true })
            yield return [culture, source, initial];
    }

    [Theory]
    [MemberData(nameof(ReadFailureCases))]
    public async Task AdministrativeReadFailuresPreserveAtomicCachesAndRecover(string culture, int source, bool initial)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        if (initial) fixture.RenameSource(source, false);
        await fixture.StartAsync();
        var inventory = fixture.Field("inventory");
        var queries = fixture.Field("queries");
        var categories = fixture.Field("categoryPaths");
        var draft = new AnalysisQueryDefinition { Kql = "Original unsaved <draft> &" };
        fixture.Set("editing", draft);
        if (!initial)
        {
            fixture.RenameSource(source, false);
            await fixture.InvokeAsync("RefreshAsync");
        }
        Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.LoadFailed"], fixture.Field("loadError"));
        Assert.Same(inventory, fixture.Field("inventory"));
        Assert.Same(queries, fixture.Field("queries"));
        Assert.Same(categories, fixture.Field("categoryPaths"));
        Assert.Same(draft, fixture.Field("editing"));
        if (initial)
            foreach (var cache in new[] { inventory, queries, categories })
                Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(cache).Cast<object>());
        Assert.IsType<SqliteException>(Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        var html = await fixture.HtmlAsync();
        Assert.Contains(fixture.Text["AdminAnalysisQueries.Errors.LoadFailed"], html);
        Assert.Matches("<button[^>]*disabled[^>]*>" + Regex.Escape(fixture.Text["AdminAnalysisQueries.InstallLibrary"]) + "</button>", html);
        Assert.DoesNotContain("disabled", Assert.Single(Regex.Matches(html,
            "<button[^>]*>" + Regex.Escape(fixture.Text["Common.Refresh"]) + "</button>").Cast<Match>()).Value);
        Assert.DoesNotContain("no such table", html);
        Assert.DoesNotContain("SQLite Error", html);
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        var writes = fixture.Writes.Attempts;
        await fixture.InvokeBlockedActionsAsync();
        Assert.Equal(writes, fixture.Writes.Attempts);
        Assert.Same(draft, fixture.Field("editing"));
        fixture.RenameSource(source, true);
        await fixture.InvokeAsync("RefreshAsync");
        Assert.Null(fixture.Field("loadError"));
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<AnalysisQueryDefinition>>(fixture.Field("queries")));
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ComputerInventoryEntry>>(fixture.Field("inventory")));
        Assert.Same(draft, fixture.Field("editing"));
        Assert.Equal("Original unsaved <draft> &", draft.Kql);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    public static IEnumerable<object[]> MutationFailureCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var action in new[] { "category", "delete", "save", "move", "bulk", "validate", "publish", "withdraw", "disable", "install" })
            yield return [culture, action];
    }

    [Theory]
    [MemberData(nameof(MutationFailureCases))]
    public async Task ConfirmedAdministrativeMutationsKeepSuccessBesideReloadFailure(string culture, string action)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        if (action is "publish" or "withdraw")
            await fixture.Registry.ValidateAsync(fixture.Query.Id, "admin");
        if (action == "withdraw")
            await fixture.Registry.PublishAsync(fixture.Query.Id, "admin");
        await fixture.StartAsync();
        var inventory = fixture.Field("inventory");
        var queries = fixture.Field("queries");
        var categories = fixture.Field("categoryPaths");
        var writes = fixture.Writes.Attempts;
        fixture.Writes.AfterSave = () => fixture.Factory.BeforeNextContext = () => fixture.RenameSource(0, false);
        string status;
        switch (action)
        {
            case "category":
                fixture.Set("categoryName", "New boundary category");
                await fixture.InvokeAsync("SaveCategoryAsync");
                status = fixture.Text["AdminAnalysisQueries.Status.CategorySaved"];
                Assert.Equal("", fixture.Field("categoryName"));
                Assert.Contains("New boundary category", await fixture.Categories.ListAsync());
                break;
            case "delete":
                await fixture.InvokeAsync("DeleteCategoryAsync", "Empty boundary category");
                status = fixture.Text["AdminAnalysisQueries.Status.CategoryDeleted"];
                Assert.DoesNotContain("Empty boundary category", await fixture.Categories.ListAsync());
                break;
            case "save":
                fixture.Set("editing", fixture.Query);
                await fixture.InvokeAsync("SaveAsync");
                status = fixture.Text["AdminAnalysisQueries.Status.DraftSaved"];
                break;
            case "move":
                fixture.Set("editing", fixture.Query);
                await fixture.InvokeAsync("MoveQueryAsync", fixture.Query.Id, "Empty boundary category");
                status = fixture.Text["AdminAnalysisQueries.Status.QueryMoved"];
                Assert.Equal("Empty boundary category", Assert.Single(await fixture.Registry.ListAsync()).Category);
                Assert.NotEqual("Empty boundary category", fixture.Query.Category);
                break;
            case "bulk":
                var keys = Assert.IsType<HashSet<string>>(fixture.Field("bulkWorkspaceKeys"));
                keys.Add("original-workspace");
                await using (var context = fixture.Factory.CreateDbContext())
                {
                    await context.AnalysisQueryWorkspaces.ExecuteUpdateAsync(updates => updates.SetProperty(workspace => workspace.Enabled, false));
                }
                await fixture.InvokeAsync("AssignWorkspacesToAllAsync");
                status = fixture.Text.Format("AdminAnalysisQueries.Status.BulkApplied", 1, 1, 0);
                Assert.True(Assert.Single(Assert.Single(await fixture.Registry.ListAsync()).Workspaces).Enabled);
                break;
            case "validate":
                await fixture.InvokeAsync("ValidateAsync", fixture.Query.Id);
                status = fixture.Text["AdminAnalysisQueries.Status.Validated"];
                Assert.Equal(AnalysisQueryStatuses.Validated, Assert.Single(await fixture.Registry.ListAsync()).Status);
                break;
            case "publish":
                await fixture.InvokeAsync("PublishAsync", fixture.Query.Id);
                status = fixture.Text["AdminAnalysisQueries.Status.Published"];
                Assert.Single(Assert.Single(await fixture.Registry.ListAsync()).Versions);
                break;
            case "withdraw":
                fixture.Set("queryToUnpublish", fixture.Query);
                await fixture.InvokeAsync("UnpublishAsync");
                status = fixture.Text["AdminAnalysisQueries.Status.Withdrawn"];
                Assert.Null(fixture.Field("queryToUnpublish"));
                Assert.Null(Assert.Single(await fixture.Registry.ListAsync()).PublishedVersionId);
                break;
            case "disable":
                await fixture.InvokeAsync("DisableAsync", fixture.Query.Id);
                status = fixture.Text["AdminAnalysisQueries.Status.Disabled"];
                Assert.False(Assert.Single(await fixture.Registry.ListAsync()).Enabled);
                break;
            case "install":
                await fixture.InvokeAsync("InstallAdLibraryAsync");
                var added = (await fixture.Registry.ListAsync()).Count - 1;
                Assert.Equal(ActiveDirectoryAnalysisQueries.Create().Count(), added);
                status = fixture.Text.Plural("AdminAnalysisQueries.Status.LibraryAdded", added);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
        Assert.Equal(status, fixture.Field("statusMessage"));
        Assert.Null(fixture.Field("errorMessage"));
        Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.LoadFailed"], fixture.Field("loadError"));
        Assert.Same(inventory, fixture.Field("inventory"));
        Assert.Same(queries, fixture.Field("queries"));
        Assert.Same(categories, fixture.Field("categoryPaths"));
        Assert.IsType<SqliteException>(Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        var html = await fixture.HtmlAsync();
        Assert.Contains(status, html);
        Assert.Contains(fixture.Text["AdminAnalysisQueries.Errors.LoadFailed"], html);
        Assert.Matches("<button[^>]*disabled[^>]*>" + Regex.Escape(fixture.Text["AdminAnalysisQueries.InstallLibrary"]) + "</button>", html);
        if (action == "publish")
            Assert.Matches("<button[^>]*disabled[^>]*>" + Regex.Escape(fixture.Text["AdminAnalysisQueries.Publish"]) + "</button>", html);
        Assert.DoesNotContain("no such table", html);
        var committedWrites = fixture.Writes.Attempts;
        Assert.True(committedWrites > writes);
        await fixture.InvokeBlockedActionsAsync();
        Assert.Equal(committedWrites, fixture.Writes.Attempts);
        Assert.Equal(status, fixture.Field("statusMessage"));
        Assert.Null(fixture.Field("errorMessage"));
        fixture.RenameSource(0, true);
        await fixture.InvokeAsync("RefreshAsync");
        Assert.Null(fixture.Field("loadError"));
        Assert.Equal(status, fixture.Field("statusMessage"));
        Assert.Equal(committedWrites, fixture.Writes.Attempts);
    }

    public static IEnumerable<object[]> OperationFailureCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var action in new[] { "pre", "post", "cancel", "permission", "concurrency", "known", "format", "import", "preview", "rejected", "busy", "missing" })
            yield return [culture, action];
    }

    public static IEnumerable<object[]> MalformedProjectionCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var shape in new[] { "missing", "wrong-type", "extra", "blank", "unexpected", "issue-parameter" })
            yield return [culture, shape];
    }

    [Theory]
    [MemberData(nameof(MalformedProjectionCases))]
    public async Task MalformedAnalysisMetadataCannotBreakTheActualAdministrativeErrorHandler(string culture, string shape)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        async Task<string> StoredSnapshotAsync() => JsonSerializer.Serialize(
            (await fixture.Registry.ListAsync()).Select(query => new
            {
                query.Id, query.Name, query.Description, query.Kql,
                Workspaces = query.Workspaces.Select(workspace => new { workspace.WorkspaceKey, workspace.Enabled })
            }));
        var stored = await StoredSnapshotAsync();
        var draft = fixture.Query;
        draft.Description = "Original <unsaved description>";
        fixture.Set("editing", draft);
        Exception failure = shape switch
        {
            "missing" => new AnalysisValidationException(AnalysisValidationCode.TimeRangeInvalid, "PRIVATE_PROVIDER_DETAIL"),
            "wrong-type" => new AnalysisValidationException(AnalysisValidationCode.TimeRangeInvalid, "PRIVATE_PROVIDER_DETAIL", "PRIVATE_ARGUMENT"),
            "extra" => new AnalysisValidationException(AnalysisValidationCode.DomainSystemsMissing, "PRIVATE_PROVIDER_DETAIL", "Original domain", "PRIVATE_ARGUMENT"),
            "blank" => new AnalysisValidationException(AnalysisValidationCode.WorkspaceSystemsMissing, "PRIVATE_PROVIDER_DETAIL", " "),
            "unexpected" => new AnalysisValidationException(AnalysisValidationCode.SystemsRequired, "PRIVATE_PROVIDER_DETAIL", "PRIVATE_ARGUMENT"),
            _ => new AnalysisQueryValidationException([
                new(AnalysisQueryValidationCode.NameInvalid, "PRIVATE_PROVIDER_DETAIL", "PRIVATE_ARGUMENT")])
        };
        var attempts = fixture.Writes.Attempts;
        fixture.Writes.NextFailure = failure;
        await fixture.InvokeAsync("SaveAsync");
        Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.SaveFailed"], fixture.Field("errorMessage"));
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        Assert.Null(fixture.Field("statusMessage"));
        Assert.Same(draft, fixture.Field("editing"));
        Assert.Equal("Original <unsaved description>", draft.Description);
        Assert.Equal(attempts + 1, fixture.Writes.Attempts);
        Assert.Equal(stored, await StoredSnapshotAsync());
        Assert.Same(failure, Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        Assert.DoesNotContain("PRIVATE_", await fixture.HtmlAsync());
        await fixture.InvokeAsync("RefreshAsync");
        Assert.Equal(attempts + 1, fixture.Writes.Attempts);
    }

    [Theory]
    [MemberData(nameof(OperationFailureCases))]
    public async Task AdministrativeErrorsAreSafeLoggedAndDoNotLoseUnconfirmedInput(string culture, string action)
    {
        using var cultures = new BoundaryCultures(culture);
        await using var fixture = await BoundaryFixture.CreateAsync();
        await fixture.StartAsync();
        var draft = fixture.Query;
        fixture.Set("editing", draft);
        var before = fixture.Writes.Attempts;
        if (action is "pre" or "cancel" or "permission" or "concurrency")
        {
            Exception failure = action switch
            {
                "cancel" => new OperationCanceledException("private cancellation diagnostic"),
                "permission" => new UnauthorizedAccessException("private permission diagnostic"),
                "concurrency" => new DbUpdateConcurrencyException("private conflict diagnostic"),
                _ => new DbUpdateException("private database diagnostic")
            };
            fixture.Writes.NextFailure = failure;
            await fixture.InvokeAsync("SaveAsync");
            var key = action switch
            {
                "permission" => "AdminAnalysisQueries.Errors.NotAllowed",
                "concurrency" => "AdminAnalysisQueries.Errors.Changed",
                _ => "AdminAnalysisQueries.Errors.SaveFailed"
            };
            Assert.Equal(fixture.Text[key], fixture.Field("errorMessage"));
            Assert.Same(failure, Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
            Assert.Same(draft, fixture.Field("editing"));
            Assert.Null(fixture.Field("statusMessage"));
            Assert.Equal(before + 1, fixture.Writes.Attempts);
        }
        else if (action == "post")
        {
            var failure = new InvalidOperationException("private committed transaction diagnostic");
            fixture.Commits.NextFailure = failure;
            fixture.Set("categoryName", "Committed category");
            await fixture.InvokeAsync("SaveCategoryAsync");
            Assert.Contains("Committed category", await fixture.Categories.ListAsync());
            Assert.Equal("Committed category", fixture.Field("categoryName"));
            Assert.Null(fixture.Field("statusMessage"));
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.CategorySaveFailed"], fixture.Field("errorMessage"));
            Assert.Same(failure, Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
            var attempts = fixture.Writes.Attempts;
            await fixture.InvokeAsync("RefreshAsync");
            Assert.Equal(attempts, fixture.Writes.Attempts);
        }
        else if (action == "known")
        {
            fixture.Set("testWorkspaceKey", "original-workspace");
            await fixture.InvokeAsync("TestAsync");
            Assert.Equal(fixture.Text["Analysis.Validation.SystemsRequired"], fixture.Field("errorMessage"));
            Assert.Equal(fixture.Field("errorMessage"), fixture.Field("testError"));
            Assert.IsType<AnalysisValidationException>(Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        }
        else if (action == "format")
        {
            draft.VisualizationJson = "{";
            await fixture.InvokeAsync("BeginEdit", draft);
            Assert.Equal("{", Assert.IsType<AnalysisQueryDefinition>(fixture.Field("editing")).VisualizationJson);
            Assert.Equal("{", draft.VisualizationJson);
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.FormatFailed"], fixture.Field("errorMessage"));
            Assert.IsAssignableFrom<JsonException>(Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        }
        else if (action == "import")
        {
            await fixture.InvokeAsync("ImportJsonAsync", "{");
            Assert.Same(draft, fixture.Field("editing"));
            Assert.Equal(fixture.Text["QueryTemplate.Validation.InvalidJson"], fixture.Field("errorMessage"));
            Assert.Equal(QueryTemplateValidationCode.InvalidJson, Assert.IsType<QueryTemplateValidationException>(
                Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception).Code);
        }
        else if (action == "preview")
        {
            fixture.Set("testWorkspaceKey", "original-workspace");
            Assert.IsType<HashSet<string>>(fixture.Field("testSystemKeys")).Add(
                fixture.System.SubscriptionId + "\u001f" + fixture.System.Computer);
            fixture.Client.Failure = new OperationCanceledException("private provider diagnostic");
            await fixture.InvokeAsync("TestAsync");
            Assert.NotNull(fixture.Field("testPreview"));
            Assert.Null(fixture.Field("testResult"));
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.TestFailed"], fixture.Field("errorMessage"));
            Assert.Equal(fixture.Field("errorMessage"), fixture.Field("testError"));
            Assert.NotNull(Assert.Single(fixture.Log.Entries, entry => entry.Exception is not null).Exception);
        }
        else if (action == "rejected")
        {
            await using (var context = fixture.Factory.CreateDbContext())
                await context.ComputerInventory.ExecuteDeleteAsync();
            await fixture.InvokeAsync("ValidateAsync", draft.Id);
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Status.ValidationResultsSaved"], fixture.Field("statusMessage"));
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.ValidationRejected"], fixture.Field("errorMessage"));
            var stored = Assert.Single(await fixture.Registry.ListAsync());
            Assert.Equal(AnalysisQueryStatuses.Draft, stored.Status);
            Assert.Equal("No inventory system routes to this workspace.", Assert.Single(stored.Workspaces).LastValidationMessage);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == LogLevel.Warning);
        }
        else if (action == "missing")
        {
            fixture.Set("editing", null);
            await fixture.InvokeAsync("SaveAsync");
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.DraftRequired"], fixture.Field("errorMessage"));
            await fixture.InvokeAsync("TestAsync");
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.DraftRequired"], fixture.Field("testError"));
            await fixture.InvokeAsync("UnpublishAsync");
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.WithdrawRequired"], fixture.Field("errorMessage"));
            await fixture.InvokeAsync("DropQueryAsync", "AD");
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.DropRequired"], fixture.Field("errorMessage"));
            fixture.Set("editing", draft);
            await fixture.InvokeAsync("AssignWorkspacesToAllAsync");
            Assert.Equal(fixture.Text["AdminAnalysisQueries.Errors.CloseEditor"], fixture.Field("errorMessage"));
            Assert.All(fixture.Log.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        }
        else if (action == "busy")
        {
            fixture.Set("isBusy", true);
            fixture.Set("statusMessage", "Original status");
            fixture.Set("errorMessage", "Original error");
            fixture.Set("draggedQueryId", draft.Id);
            foreach (var method in new[] { "SaveAsync", "SaveCategoryAsync", "InstallAdLibraryAsync", "AssignWorkspacesToAllAsync",
                "UnpublishAsync", "TestAsync", "RefreshAsync" })
                await fixture.InvokeAsync(method);
            foreach (var method in new[] { "ValidateAsync", "PublishAsync", "DisableAsync" })
                await fixture.InvokeAsync(method, draft.Id);
            await fixture.InvokeAsync("MoveQueryAsync", draft.Id, "AD");
            await fixture.InvokeAsync("DropQueryAsync", "AD");
            await fixture.InvokeAsync("DeleteCategoryAsync", "AD");
            await fixture.InvokeAsync("ImportJsonAsync", "{");
            Assert.Equal("Original status", fixture.Field("statusMessage"));
            Assert.Equal("Original error", fixture.Field("errorMessage"));
            Assert.Same(draft, fixture.Field("editing"));
            Assert.Equal(draft.Id, fixture.Field("draggedQueryId"));
            Assert.Empty(fixture.Log.Entries);
            fixture.Set("isBusy", false);
        }
        else throw new ArgumentOutOfRangeException(nameof(action));
        Assert.False(Assert.IsType<bool>(fixture.Field("isBusy")));
        Assert.Null(fixture.Field("loadError"));
        var html = await fixture.HtmlAsync();
        Assert.DoesNotContain("private ", html);
        Assert.DoesNotContain("no such table", html);
        if (action is not ("pre" or "post" or "cancel" or "permission" or "concurrency" or "rejected"))
            Assert.Equal(before, fixture.Writes.Attempts);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    private sealed class BoundaryCultures : IDisposable
    {
        private readonly CultureInfo ui = CultureInfo.CurrentUICulture;
        private readonly CultureInfo data = CultureInfo.CurrentCulture;
        public BoundaryCultures(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.CurrentCulture = data;
        }
    }

    private sealed class BoundaryFixture(
        SqliteConnection connection, ContextFactory factory, BoundaryWrites writes, BoundaryCommits commits,
        AnalysisQueryRegistry registry, AnalysisQueryCategoryService categories, LocalQueryClient client,
        AnalysisQueryDefinition query, ComputerInventoryEntry system, ServiceProvider services,
        StaticRenderer renderer, CapturingActivator activator, PageLog log) : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private Func<string>? html;
        public ContextFactory Factory { get; } = factory;
        public BoundaryWrites Writes { get; } = writes;
        public BoundaryCommits Commits { get; } = commits;
        public AnalysisQueryRegistry Registry { get; } = registry;
        public AnalysisQueryCategoryService Categories { get; } = categories;
        public LocalQueryClient Client { get; } = client;
        public AnalysisQueryDefinition Query { get; } = query;
        public ComputerInventoryEntry System { get; } = system;
        public PageLog Log { get; } = log;
        public UiText Text => services.GetRequiredService<UiText>();
        public AdminAnalysisQueries Page => Assert.IsType<AdminAnalysisQueries>(activator.Page);
        public object? Field(string name) => typeof(AdminAnalysisQueries).GetField(name, Flags)!.GetValue(Page);
        public void Set(string name, object? value) => typeof(AdminAnalysisQueries).GetField(name, Flags)!.SetValue(Page, value);
        public async Task InvokeAsync(string name, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(AdminAnalysisQueries).GetMethod(name, Flags)!.Invoke(Page, arguments);
                if (result is Task task) await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Page, []);
            });
        }
        public async Task InvokeBlockedActionsAsync()
        {
            foreach (var method in new[] { "SaveAsync", "SaveCategoryAsync", "InstallAdLibraryAsync",
                "AssignWorkspacesToAllAsync", "UnpublishAsync", "TestAsync" })
                await InvokeAsync(method);
            foreach (var method in new[] { "ValidateAsync", "PublishAsync", "DisableAsync" })
                await InvokeAsync(method, Query.Id);
            await InvokeAsync("MoveQueryAsync", Query.Id, "AD");
            await InvokeAsync("DropQueryAsync", "AD");
            await InvokeAsync("DeleteCategoryAsync", "AD");
            await InvokeAsync("ImportJsonAsync", "{");
        }
        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(Assert.IsType<Func<string>>(html)()));
        public async Task StartAsync()
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                            [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, SqlAuthenticationRoles.Admin)], "test")))),
                        ["ChildContent"] = (RenderFragment)(builder =>
                        {
                            builder.OpenComponent<AdminAnalysisQueries>(0);
                            builder.CloseComponent();
                        })
                    }));
                await root.QuiescenceTask;
                html = root.ToHtmlString;
            });
        }
        public void RenameSource(int source, bool restore)
        {
            using var command = connection.CreateCommand();
            command.CommandText = (source, restore) switch
            {
                (0, false) => "ALTER TABLE ComputerInventory RENAME TO FailedInventory",
                (0, true) => "ALTER TABLE FailedInventory RENAME TO ComputerInventory",
                (1, false) => "ALTER TABLE AnalysisQueryDefinitions RENAME TO FailedQueries",
                (1, true) => "ALTER TABLE FailedQueries RENAME TO AnalysisQueryDefinitions",
                (2, false) => "ALTER TABLE AnalysisQueryCategories RENAME TO FailedCategories",
                (2, true) => "ALTER TABLE FailedCategories RENAME TO AnalysisQueryCategories",
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            };
            command.ExecuteNonQuery();
        }
        public static async Task<BoundaryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var writes = new BoundaryWrites();
            var commits = new BoundaryCommits();
            var factory = new ContextFactory(new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlite(connection).AddInterceptors(writes, commits).Options);
            await using (var context = factory.CreateDbContext())
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                        .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                        .Replace("N'", "'", StringComparison.Ordinal));
            var options = new CrossTenantLogAnalyticsOptions
            {
                Enabled = true, ApplicationClientId = "local-test",
                Workspaces = [new() { Key = "original-workspace", Domains = ["original.example"] }]
            };
            var client = new LocalQueryClient();
            var runner = new AnalysisQueryRunner(options, new LocalSecretProvider(), new TargetWorkspaceResolver(options), client);
            var registry = new AnalysisQueryRegistry(factory, options, runner);
            var categories = new AnalysisQueryCategoryService(factory);
            await categories.CreateAsync("Empty boundary category", "");
            var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-fsmo-roles");
            query.Key = "boundary-query";
            query.Workspaces.Add(new() { WorkspaceKey = "original-workspace", Enabled = true });
            query = await registry.SaveDraftAsync(query, "admin");
            var system = new ComputerInventoryEntry
            {
                Computer = "Lifecycle-DC", Domain = "original.example",
                SubscriptionId = "11111111-1111-1111-1111-111111111111"
            };
            await using (var context = factory.CreateDbContext())
            {
                context.ComputerInventory.Add(system);
                await context.SaveChangesAsync();
            }
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            var store = new AlertStore(factory,
                new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder()),
                presenter, new AlertRuleEvaluator(presenter), new StoreLog());
            var activator = new CapturingActivator();
            var log = new PageLog();
            var services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton<IComponentActivator>(activator).AddSingleton<ILogger<AdminAnalysisQueries>>(log)
                .AddSingleton(registry).AddSingleton(categories).AddSingleton(options).AddSingleton(runner)
                .AddSingleton(new ApplicationAuthenticationOptions())
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor())).AddSingleton(store).BuildServiceProvider();
            var renderer = new StaticRenderer(services, services.GetRequiredService<ILoggerFactory>());
            return new(connection, factory, writes, commits, registry, categories, client, query, system, services, renderer, activator, log);
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class BoundaryWrites : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public Exception? NextFailure { get; set; }
        public Action? AfterSave { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            var failure = NextFailure;
            NextFailure = null;
            if (failure is not null) throw failure;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var after = AfterSave;
            AfterSave = null;
            after?.Invoke();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BoundaryCommits : DbTransactionInterceptor
    {
        public Exception? NextFailure { get; set; }
        public override Task TransactionCommittedAsync(
            System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            var failure = NextFailure;
            NextFailure = null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private sealed class PageLog : ILogger<AdminAnalysisQueries>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, exception));
    }

    private sealed class ContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public Action? BeforeNextContext { get; set; }
        public AlertDbContext CreateDbContext()
        {
            var before = BeforeNextContext;
            BeforeNextContext = null;
            before?.Invoke();
            return new(options);
        }
    }

    private sealed class LocalQueryClient : IAnalysisLogAnalyticsQuery
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId,
            string secret, string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) return Task.FromException<LogsQueryResult>(Failure);
            return Task.FromResult(ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromObjectAsJson(new
            {
                tables = new[]
                {
                    new
                    {
                        name = "PrimaryResult",
                        columns = new[] { new { name = "Computer", type = "string" } },
                        rows = new[] { new[] { "Lifecycle-DC" } }
                    }
                }
            }))!);
        }
    }

    private sealed class LocalSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) => Task.FromResult("not-a-real-secret");
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public AdminAnalysisQueries? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is AdminAnalysisQueries page) Page = page;
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

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Rendering query administration must not access Azure credentials.");
    }
}
