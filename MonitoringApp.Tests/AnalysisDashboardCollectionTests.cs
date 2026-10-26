using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
        Assert.Throws<InvalidOperationException>(restored.DeleteActive);
        Assert.Single(restored.Dashboards);
        Assert.Throws<InvalidOperationException>(() => restored.Select(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RejectsInvalidNamesWithoutMutatingCollection(string name)
    {
        var collection = AnalysisDashboardCollection.Create();
        Assert.Throws<InvalidOperationException>(() => collection.Add(name));
        Assert.Throws<InvalidOperationException>(() => collection.RenameActive(name));
        Assert.Single(collection.Dashboards);
        Assert.Throws<InvalidOperationException>(() => collection.Add(new string('x', 257)));
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
        Assert.Contains("Create dashboard", html);
        Assert.Contains("Rename", html);
        Assert.Contains("Delete dashboard", html);
        Assert.Contains("maxlength=\"256\"", html);
    }

    [Fact]
    public async Task CompactTabsKeepDashboardSelectionAndHideManagement()
    {
        var collection = AnalysisDashboardCollection.Create();
        collection.RenameActive("AD operations");
        collection.Add("Replication");
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
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
        Assert.DoesNotContain("Create dashboard", html);
        Assert.DoesNotContain("Rename", html);
        Assert.DoesNotContain("Delete dashboard", html);
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

    private static async Task<string> RenderAsync(AnalysisDashboardCollection collection)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
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
