using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardCollectionTests
{
    [Fact]
    public void MigratesExistingDashboardWithoutLosingLayoutSystemsOrPreferences()
    {
        var query = Guid.NewGuid();
        var original = new AnalysisDashboardDocument
        {
            Name = "AD operations", Columns = 5, Rows = 20, RowHeight = 100,
            AutoRefreshEnabled = true, HiddenQueryIds = [query],
            Systems = [new() { SubscriptionId = "subscription", Computer = "DC01" }],
            Widgets = [new() { QueryDefinitionId = query, BinMinutes = 30, GroupBy = "NC", TimeRangeMinutes = 10080 }]
        };
        var legacyJson = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var collection = AnalysisDashboardCollection.Deserialize(legacyJson);
        var migrated = collection.OpenActive();

        Assert.Single(collection.Dashboards);
        Assert.Equal("AD operations", migrated.Name);
        Assert.Equal(5, migrated.Columns);
        Assert.Equal(20, migrated.Rows);
        Assert.Equal(100, migrated.RowHeight);
        Assert.True(migrated.AutoRefreshEnabled);
        Assert.Equal("DC01", Assert.Single(migrated.Systems).Computer);
        Assert.Equal(30, Assert.Single(migrated.Widgets).BinMinutes);
        Assert.Equal("NC", migrated.Widgets[0].GroupBy);
        Assert.Equal(10080, migrated.Widgets[0].TimeRangeMinutes);
        Assert.Contains(query, collection.HiddenQueryIds);
        var restored = AnalysisDashboardCollection.Deserialize(collection.Serialize());
        Assert.Equal(collection.ActiveDashboardId, restored.ActiveDashboardId);
        Assert.Equal(2, restored.Version);
        Assert.DoesNotContain("\"active\":", collection.Serialize());
    }

    [Fact]
    public void KeepsDashboardsIndependentAndSharesOnlyHiddenQueries()
    {
        var query = Guid.NewGuid();
        var collection = AnalysisDashboardCollection.Create();
        var firstId = collection.ActiveDashboardId;
        var state = new AnalysisDashboardState();
        state.Load(collection.OpenActive());
        state.AddWidget(query, "CPU", 60);
        state.UpdateWorkspace(document =>
        {
            document.AutoRefreshEnabled = true;
            document.Systems.Add(new() { SubscriptionId = "subscription", Computer = "DC01" });
        });
        state.HideQuery(query);
        collection.CaptureActive(state.Document);

        collection.Add("Replication");
        var secondId = collection.ActiveDashboardId;
        state.Load(collection.OpenActive());
        Assert.Empty(state.Document.Widgets);
        Assert.Empty(state.Document.Systems);
        Assert.False(state.Document.AutoRefreshEnabled);
        Assert.Contains(query, state.Document.HiddenQueryIds);
        state.ResizeGrid(6, 25, 110);
        state.RestoreQuery(query);
        collection.CaptureActive(state.Document);

        collection.Select(firstId);
        state.Load(collection.OpenActive());
        Assert.True(state.Document.AutoRefreshEnabled);
        Assert.Single(state.Document.Widgets);
        Assert.Single(state.Document.Systems);
        Assert.Empty(state.Document.HiddenQueryIds);
        Assert.Equal(4, state.Document.Columns);
        state.Document.Widgets.Clear();
        Assert.Single(collection.Active.Document.Widgets);
        collection.Select(secondId);
        Assert.Equal(6, collection.OpenActive().Columns);
        Assert.Equal(25, collection.OpenActive().Rows);
    }

    [Fact]
    public void DuplicatesCompleteDashboardWithoutSharingStateOrPublicationIdentity()
    {
        var source = new AnalysisDashboardDocument
        {
            Name = "AD operations", Columns = 6, Rows = 20, RowHeight = 100,
            AutoRefreshEnabled = true, TimeRangeMinutesOverride = 10080, BinMinutesOverride = 30,
            SourceTemplateId = Guid.NewGuid(), SourceTemplateVersion = 4,
            HiddenQueryIds = [Guid.NewGuid()],
            SystemsPanel = new() { Width = 600, Height = 400 },
            Systems = [new() { SubscriptionId = "sub", Computer = "DC01" }],
            Widgets = [new() { QueryDefinitionId = Guid.NewGuid(), Title = "LDAP", Row = 4, ColumnSpan = 3, BinMinutes = 60, GroupBy = "NC" }]
        };
        var collection = AnalysisDashboardCollection.Create(source);
        var originalId = collection.ActiveDashboardId;
        var hidden = Guid.NewGuid();
        collection.HiddenQueryIds = [hidden];
        collection.AddCopy(source);
        var copyId = collection.ActiveDashboardId;
        var copy = collection.Active.Document;
        Assert.NotEqual(originalId, copyId);
        Assert.Equal("AD operations (copy)", copy.Name);
        Assert.Equal(6, copy.Columns);
        Assert.Equal(20, copy.Rows);
        Assert.Equal(100, copy.RowHeight);
        Assert.True(copy.AutoRefreshEnabled);
        Assert.Equal(10080, copy.TimeRangeMinutesOverride);
        Assert.Equal(30, copy.BinMinutesOverride);
        Assert.Equal(600, copy.SystemsPanel.Width);
        Assert.Equal("DC01", Assert.Single(copy.Systems).Computer);
        var widget = Assert.Single(copy.Widgets);
        Assert.Equal(source.Widgets[0].QueryDefinitionId, widget.QueryDefinitionId);
        Assert.NotEqual(source.Widgets[0].Id, widget.Id);
        Assert.Equal(4, widget.Row);
        Assert.Equal(3, widget.ColumnSpan);
        Assert.Equal(60, widget.BinMinutes);
        Assert.Equal("NC", widget.GroupBy);
        Assert.Null(copy.SourceTemplateId);
        Assert.Null(copy.SourceTemplateVersion);
        Assert.Equal([hidden], copy.HiddenQueryIds);
        widget.Title = "Changed copy";
        copy.Systems[0].Computer = "DC02";
        copy.SystemsPanel.Width = 800;
        Assert.Equal("LDAP", source.Widgets[0].Title);
        Assert.Equal("DC01", source.Systems[0].Computer);
        Assert.Equal(600, source.SystemsPanel.Width);
        var restored = AnalysisDashboardCollection.Deserialize(collection.Serialize());
        Assert.Equal(copyId, restored.ActiveDashboardId);
        Assert.Equal(2, restored.Dashboards.Count);
        Assert.Equal("Changed copy", restored.Active.Document.Widgets[0].Title);
    }

    [Fact]
    public void DuplicateNamesRemainDistinctAndWithinTheExistingLengthLimit()
    {
        var source = new AnalysisDashboardDocument { Name = new string('x', 256) };
        var collection = AnalysisDashboardCollection.Create(source);
        collection.AddCopy(source);
        Assert.Equal(256, collection.Active.Document.Name.Length);
        Assert.EndsWith(" (copy)", collection.Active.Document.Name);
        collection.AddCopy(source);
        Assert.Equal(256, collection.Active.Document.Name.Length);
        Assert.EndsWith(" (copy 2)", collection.Active.Document.Name);
        Assert.Equal(3, collection.Dashboards.Select(entry => entry.Document.Name).Distinct().Count());
        Assert.Equal(AnalysisValidationCode.DashboardNameInvalid,
            Assert.Throws<AnalysisValidationException>(() => collection.AddCopy(new() { Name = "" })).Code);
        Assert.Equal(3, collection.Dashboards.Count);
    }

    [Fact]
    public void RenameDeleteAndReloadRetainActiveTabAndSharedPreferences()
    {
        var collection = AnalysisDashboardCollection.Create();
        var firstId = collection.ActiveDashboardId;
        collection.HiddenQueryIds.Add(Guid.NewGuid());
        collection.Add("Second");
        collection.RenameActive("  Replication health  ");
        var secondId = collection.ActiveDashboardId;
        collection.Add("Third");
        collection.Select(secondId);

        var restored = AnalysisDashboardCollection.Deserialize(collection.Serialize());
        Assert.Equal(secondId, restored.ActiveDashboardId);
        Assert.Equal("Replication health", restored.Active.Document.Name);
        restored.DeleteActive();
        Assert.Equal(2, restored.Dashboards.Count);
        Assert.DoesNotContain(restored.Dashboards, entry => entry.Id == secondId);
        Assert.Equal("Third", restored.Active.Document.Name);
        Assert.Single(restored.HiddenQueryIds);
        restored.DeleteActive();
        Assert.Equal(firstId, restored.ActiveDashboardId);
        Assert.Equal(AnalysisValidationCode.KeepDashboard, Assert.Throws<AnalysisValidationException>(restored.DeleteActive).Code);
        Assert.Single(restored.Dashboards);
        Assert.Equal(AnalysisValidationCode.DashboardMissing,
            Assert.Throws<AnalysisValidationException>(() => restored.Select(Guid.NewGuid())).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RejectsInvalidNamesWithoutMutatingCollection(string name)
    {
        var collection = AnalysisDashboardCollection.Create();
        Assert.Throws<AnalysisValidationException>(() => collection.Add(name));
        Assert.Throws<AnalysisValidationException>(() => collection.RenameActive(name));
        Assert.Single(collection.Dashboards);
        Assert.Throws<AnalysisValidationException>(() => collection.Add(new string('x', 257)));
    }

    [Fact]
    public void RejectsInvalidCollectionsRatherThanSilentlyDroppingDashboards()
    {
        Assert.Throws<InvalidOperationException>(() => AnalysisDashboardCollection.Deserialize(
            """{"version":2,"activeDashboardId":"00000000-0000-0000-0000-000000000001","dashboards":[]}"""));
        var collection = AnalysisDashboardCollection.Create();
        collection.Dashboards.Add(collection.Dashboards[0]);
        Assert.Throws<InvalidOperationException>(() => collection.Serialize());
    }

    [Fact]
    public async Task RendersDynamicButtonsNamesAndManagementControls()
    {
        var collection = AnalysisDashboardCollection.Create();
        collection.RenameActive("<AD>");
        collection.Add("Replication");
        collection.Add("LDAP latency");
        var html = await RenderAsync(collection);
        Assert.Contains("&lt;AD&gt;", html);
        Assert.Contains("Replication", html);
        Assert.Contains("LDAP latency", html);
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.Contains(UiLabel("AnalysisDashboards.Create"), WebUtility.HtmlDecode(html));
        Assert.Contains(UiLabel("Common.Rename"), WebUtility.HtmlDecode(html));
        Assert.Contains(UiLabel("AnalysisDashboards.Delete"), WebUtility.HtmlDecode(html));
        Assert.Contains("maxlength=\"256\"", html);
    }

    [Fact]
    public async Task CompactTabsKeepDashboardSelectionAndHideManagement()
    {
        var collection = AnalysisDashboardCollection.Create();
        collection.RenameActive("AD operations");
        collection.Add("Replication");
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddUiLocalization();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisDashboardTabs>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Collection"] = collection, ["EditMode"] = false
                }))).ToHtmlString());
        Assert.Contains("AD operations", html);
        Assert.Contains("Replication", html);
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.DoesNotContain(UiLabel("AnalysisDashboards.Create"), WebUtility.HtmlDecode(html));
        Assert.DoesNotContain(UiLabel("Common.Rename"), WebUtility.HtmlDecode(html));
        Assert.DoesNotContain(UiLabel("AnalysisDashboards.Delete"), WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task PersistsMultipleDashboardsPerOwnerMigratesLegacyAndRejectsStaleSaves()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await using (var context = factory.CreateDbContext())
        {
            var statements = context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisDashboards\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisDashboardTemplates\" ", StringComparison.Ordinal));
            foreach (var statement in statements)
                await context.Database.ExecuteSqlRawAsync(statement
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            context.AnalysisDashboards.Add(new()
            {
                OwnerKey = "alice@example.com", Name = "Existing", Revision = 4, DocumentVersion = 1,
                DocumentJson = """{"name":"Existing","columns":5,"autoRefreshEnabled":true}"""
            });
            await context.SaveChangesAsync();
        }
        var repository = new AnalysisDashboardRepository(factory);
        var loaded = await repository.LoadAsync("alice@example.com");
        Assert.Equal(4, loaded.Revision);
        var collection = loaded.Collection!;
        Assert.Equal("Existing", collection.Active.Document.Name);
        var existingId = collection.ActiveDashboardId;
        collection.Add("Certificates");
        var saved = await repository.SaveAsync("alice@example.com", collection, loaded.Revision);
        Assert.Equal(5, saved.Revision);
        var reloaded = await repository.LoadAsync("alice@example.com");
        Assert.Equal(2, reloaded.Collection!.Dashboards.Count);
        Assert.Equal("Certificates", reloaded.Collection.Active.Document.Name);
        Assert.True(reloaded.Collection.Dashboards.Single(entry => entry.Id == existingId).Document.AutoRefreshEnabled);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            repository.SaveAsync("alice@example.com", collection, loaded.Revision));

        Assert.Null((await repository.LoadAsync("bob@example.com")).Collection);
        var bob = AnalysisDashboardCollection.Create();
        await repository.SaveAsync("bob@example.com", bob, 0);
        Assert.Single((await repository.LoadAsync("bob@example.com")).Collection!.Dashboards);
        Assert.Equal(2, (await repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(2, await verify.AnalysisDashboards.CountAsync());
        Assert.Equal(2, (await verify.AnalysisDashboards.SingleAsync(row => row.OwnerKey == "alice@example.com")).DocumentVersion);
    }

    [Theory]
    [InlineData("en-US", 1, true, false)]
    [InlineData("de-DE", 1, true, false)]
    [InlineData("en-US", 2, true, false)]
    [InlineData("de-DE", 2, true, false)]
    [InlineData("en-US", 2, true, true)]
    [InlineData("de-DE", 2, true, true)]
    [InlineData("en-US", 2, false, false)]
    [InlineData("de-DE", 2, false, false)]
    public async Task PersonalDashboardControlsTranslateWithoutChangingNamesSelectionFormLimitsOrDisabledRules(
        string culture, int count, bool edit, bool disabled)
    {
        using var selection = new UiSelection(culture);
        var collection = AnalysisDashboardCollection.Create();
        var defaultName = collection.Active.Document.Name;
        if (count == 2) collection.Add("Original <dashboard> & value");
        var original = collection.Serialize();
        await using var fixture = await TabsFixture.Create(collection, new()
        {
            ["EditMode"] = edit, ["Disabled"] = disabled
        });
        var html = fixture.Html;
        var decoded = WebUtility.HtmlDecode(html);
        Assert.Contains($"aria-label=\"{(culture == "de-DE" ? "Persönliche Dashboards" : "Personal dashboards")}\"", decoded);
        Assert.Contains($"aria-label=\"{(culture == "de-DE" ? "Dashboard auswählen" : "Select dashboard")}\"", decoded);
        var navigation = Regex.Match(html, "<nav.*?</nav>", RegexOptions.Singleline).Value;
        var buttons = Regex.Matches(navigation, "<button[^>]*>(.*?)</button>", RegexOptions.Singleline);
        Assert.Equal(collection.Dashboards.Select(entry => entry.Document.Name),
            buttons.Select(button => WebUtility.HtmlDecode(button.Groups[1].Value)));
        Assert.Single(Regex.Matches(navigation, "aria-pressed=\"true\""));
        Assert.All(buttons, button => Assert.Equal(disabled, button.Value.Contains("disabled")));
        Assert.Contains(defaultName, decoded);
        if (count == 2) Assert.Contains("Original &lt;dashboard&gt; &amp; value", html);
        Assert.Equal(edit ? 2 : 0, Regex.Matches(html, "<form\\b").Count);
        if (edit)
        {
            foreach (var label in culture == "de-DE"
                ? new[] { "Name des neuen Dashboards", "Dashboard erstellen", "Name des aktuellen Dashboards", "Umbenennen", "Dashboard löschen" }
                : new[] { "New dashboard name", "Create dashboard", "Current dashboard name", "Rename", "Delete dashboard" })
                Assert.Contains(label, decoded);
            var inputs = Regex.Matches(html, "<input[^>]*>");
            Assert.Equal(2, inputs.Count);
            Assert.All(inputs, input =>
            {
                Assert.Contains("required", input.Value);
                Assert.Contains("maxlength=\"256\"", input.Value);
                Assert.Equal(disabled, input.Value.Contains("disabled"));
            });
            var delete = Regex.Match(html, "<button class=\"admin-danger\"[^>]*>").Value;
            Assert.Equal(disabled || count == 1, delete.Contains("disabled"));
        }
        Assert.DoesNotContain("role=\"alertdialog\"", html);
        Assert.Equal(original, collection.Serialize());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task PersonalDashboardDeleteConfirmationUsesWholeSafeMessageAndRetainsDraftAndResetSemantics(
        string culture, bool disabled)
    {
        using var selection = new UiSelection(culture);
        const string name = "Original {0} <script> & dashboard";
        var collection = AnalysisDashboardCollection.Create();
        collection.Add(name);
        var original = collection.Serialize();
        await using var fixture = await TabsFixture.Create(collection, new() { ["Disabled"] = disabled });
        await fixture.Set("newName", "Original create draft");
        await fixture.Set("currentName", "Original rename draft");
        await fixture.Set("confirmDelete", true);
        await fixture.ApplyParameters();
        Assert.Equal("Original create draft", fixture.Get<string>("newName"));
        Assert.Equal("Original rename draft", fixture.Get<string>("currentName"));
        var html = fixture.Html;
        Assert.Contains("role=\"alertdialog\" aria-labelledby=\"delete-dashboard-heading\"", html);
        var dialog = Regex.Match(html, "<div class=\"admin-message admin-message-warning\".*?</div>", RegexOptions.Singleline).Value;
        var decoded = WebUtility.HtmlDecode(dialog);
        Assert.Contains(culture == "de-DE" ? "Persönliches Dashboard löschen?" : "Delete personal dashboard?", decoded);
        var body = Regex.Match(dialog, "<p>(.*?)</p>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal(name, WebUtility.HtmlDecode(Regex.Match(body, "<strong>(.*?)</strong>").Groups[1].Value));
        var expected = culture == "de-DE"
            ? $"Dashboard {name} samt Widget-Layout löschen? Andere Dashboards, Abfragedefinitionen und gemeinsame Bibliothekseinstellungen bleiben erhalten. Dies kann nicht rückgängig gemacht werden."
            : $"Delete {name} and its widget layout? Other dashboards, query definitions and shared library preferences will be retained. This cannot be undone.";
        Assert.Equal(expected, WebUtility.HtmlDecode(Regex.Replace(body, "</?strong>", "")));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains(culture == "de-DE" ? "Abbrechen" : "Cancel", decoded);
        Assert.Contains(culture == "de-DE" ? "Löschen bestätigen" : "Confirm delete", decoded);
        Assert.All(Regex.Matches(dialog, "<button[^>]*>"), button => Assert.Equal(disabled, button.Value.Contains("disabled")));
        Assert.Equal(original, collection.Serialize());
        fixture.Parameters["EditMode"] = false;
        await fixture.ApplyParameters();
        Assert.False(fixture.Get<bool>("confirmDelete"));
        Assert.DoesNotContain("role=\"alertdialog\"", fixture.Html);
        fixture.Parameters["EditMode"] = true;
        await fixture.ApplyParameters();
        await fixture.Set("confirmDelete", true);
        collection.RenameActive("Original replacement name");
        await fixture.ApplyParameters();
        Assert.False(fixture.Get<bool>("confirmDelete"));
        Assert.Equal("Original replacement name", fixture.Get<string>("currentName"));
        Assert.Equal("Original create draft", fixture.Get<string>("newName"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PersonalDashboardCallbacksKeepOriginalArgumentsAndCollectionLayoutLibraryAndSelectionContracts(string culture)
    {
        using var selection = new UiSelection(culture);
        var collection = AnalysisDashboardCollection.Create();
        var firstId = collection.ActiveDashboardId;
        var queryId = Guid.NewGuid();
        collection.HiddenQueryIds.Add(queryId);
        collection.Active.Document.Widgets.Add(new()
        {
            Id = "original-widget", QueryDefinitionId = queryId, Title = "Original widget",
            GroupBy = "NC", TimeRangeMinutes = 10080, BinMinutes = 30
        });
        var first = JsonSerializer.Serialize(collection.Active.Document);
        var created = "";
        var renamed = "";
        var selected = Guid.Empty;
        var deletes = 0;
        var receiver = new object();
        await using var fixture = await TabsFixture.Create(collection, new()
        {
            ["Created"] = EventCallback.Factory.Create<string>(receiver, value => { created = value; collection.Add(value); }),
            ["Renamed"] = EventCallback.Factory.Create<string>(receiver, value => { renamed = value; collection.RenameActive(value); }),
            ["Selected"] = EventCallback.Factory.Create<Guid>(receiver, value => { selected = value; collection.Select(value); }),
            ["Deleted"] = EventCallback.Factory.Create(receiver, () => { deletes++; collection.DeleteActive(); })
        });
        const string newName = "  Original <created> & dashboard  ";
        await fixture.Set("newName", newName);
        await fixture.Invoke("CreateAsync");
        Assert.Equal(newName, created);
        Assert.Equal(newName.Trim(), collection.Active.Document.Name);
        Assert.Equal(newName.Trim(), fixture.Get<string>("currentName"));
        var secondId = collection.ActiveDashboardId;
        const string rename = "  Original {0} renamed dashboard  ";
        await fixture.Set("currentName", rename);
        await fixture.Invoke("RenameAsync");
        Assert.Equal(rename, renamed);
        Assert.Equal(rename.Trim(), collection.Active.Document.Name);
        await fixture.Set("confirmDelete", true);
        await fixture.Select(firstId);
        Assert.Equal(firstId, selected);
        Assert.False(fixture.Get<bool>("confirmDelete"));
        Assert.Equal(collection.Active.Document.Name, fixture.Get<string>("currentName"));
        Assert.Equal(first, JsonSerializer.Serialize(collection.Active.Document));
        await fixture.Select(secondId);
        await fixture.Invoke("ConfirmDelete");
        await fixture.Invoke("DeleteAsync");
        Assert.Equal(1, deletes);
        Assert.Equal(firstId, collection.ActiveDashboardId);
        Assert.Single(collection.Dashboards);
        Assert.Equal(queryId, Assert.Single(collection.HiddenQueryIds));
        Assert.Equal(first, JsonSerializer.Serialize(collection.Active.Document));
        Assert.False(fixture.Get<bool>("confirmDelete"));
        Assert.DoesNotContain("role=\"alertdialog\"", fixture.Html);
        Assert.Equal(collection.Serialize(), AnalysisDashboardCollection.Deserialize(collection.Serialize()).Serialize());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    private static string UiLabel(string key)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddUiLocalization();
        using var services = registrations.BuildServiceProvider();
        return services.GetRequiredService<UiText>()[key];
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previousUi = CultureInfo.CurrentUICulture;
        private readonly CultureInfo previousFormat = CultureInfo.CurrentCulture;
        public UiSelection(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }

    private sealed class TabsActivator(IServiceProvider services) : IComponentActivator
    {
        public AnalysisDashboardTabs Tabs { get; private set; } = default!;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is AnalysisDashboardTabs tabs) Tabs = tabs;
            return component;
        }
    }

    private sealed class TabsFixture(ServiceProvider services, HtmlRenderer renderer) : IAsyncDisposable
    {
        public Dictionary<string, object?> Parameters { get; } = [];
        private AnalysisDashboardTabs Tabs => services.GetRequiredService<TabsActivator>().Tabs;
        private HtmlRootComponent root;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public T Get<T>(string field) => (T)typeof(AnalysisDashboardTabs).GetField(field, Flags)!.GetValue(Tabs)!;
        public Task ApplyParameters() => renderer.Dispatcher.InvokeAsync(() => Tabs.SetParametersAsync(ParameterView.FromDictionary(Parameters)));
        public Task Set(string field, object value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(AnalysisDashboardTabs).GetField(field, Flags)!.SetValue(Tabs, value);
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Tabs, null);
        });
        public Task Invoke(string method) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(AnalysisDashboardTabs).GetMethod(method, Flags)!.Invoke(Tabs, null)!;
            await Tabs.SetParametersAsync(ParameterView.FromDictionary(Parameters));
        });
        public Task Select(Guid id) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            await Tabs.Selected.InvokeAsync(id);
            await Tabs.SetParametersAsync(ParameterView.FromDictionary(Parameters));
        });
        public static async Task<TabsFixture> Create(AnalysisDashboardCollection collection, Dictionary<string, object?> parameters)
        {
            var registrations = new ServiceCollection().AddLogging();
            registrations.AddUiLocalization();
            registrations.AddSingleton<TabsActivator>();
            registrations.AddSingleton<IComponentActivator>(services => services.GetRequiredService<TabsActivator>());
            var services = registrations.BuildServiceProvider();
            var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var fixture = new TabsFixture(services, renderer);
            fixture.Parameters["Collection"] = collection;
            foreach (var parameter in parameters) fixture.Parameters[parameter.Key] = parameter.Value;
            fixture.root = await renderer.Dispatcher.InvokeAsync(() =>
                renderer.RenderComponentAsync<AnalysisDashboardTabs>(ParameterView.FromDictionary(fixture.Parameters)));
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private static async Task<string> RenderAsync(AnalysisDashboardCollection collection)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddUiLocalization();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisDashboardTabs>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Collection"] = collection }))).ToHtmlString());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }
}
