using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class AnalysisQueryCategoryTests
{
    [Fact]
    public async Task ImportsExistingCategoriesAndPersistsEmptyNestedCategories()
    {
        await using var fixture = await Fixture.CreateAsync();
        var query = await fixture.SeedAsync("AD");
        Assert.Equal(["AD"], await fixture.Categories.ListAsync());
        await fixture.Categories.CreateAsync(" Replication ", "ad");
        await fixture.Categories.CreateAsync("Errors", "AD/Replication");
        Assert.Equal(["AD", "AD/Replication", "AD/Replication/Errors"], await fixture.Categories.ListAsync());
        await fixture.Categories.MoveQueryAsync(query.Id, "AD/Replication/Errors", "admin");
        await fixture.Categories.MoveQueryAsync(query.Id, "AD", "admin");
        Assert.Contains("AD/Replication/Errors", await new AnalysisQueryCategoryService(fixture.Factory).ListAsync());
        await fixture.Categories.DeleteAsync("AD/Replication/Errors");
        await fixture.Categories.DeleteAsync("AD/Replication");
        Assert.Equal(["AD"], await fixture.Categories.ListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.DeleteAsync("AD"));
    }

    [Fact]
    public async Task MoveIsImmediateForPublishedQueriesWithoutPublishingDraftChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.SeedAsync("AD");
        saved.MinimumRole = SqlAuthenticationRoles.Admin;
        await fixture.Registry.SaveDraftAsync(saved, "admin");
        await fixture.PublishAsync(saved.Id);
        var before = Assert.Single(await fixture.Registry.ListPublishedAsync());
        var draft = Assert.Single(await fixture.Registry.ListAsync());
        draft.Kql = "Unfinished KQL draft";
        draft.Name = "Unpublished name";
        draft.MinimumRole = SqlAuthenticationRoles.Operator;
        await fixture.Registry.SaveDraftAsync(draft, "draft-author");
        await fixture.Categories.CreateAsync("Replication", "AD");
        await fixture.Categories.MoveQueryAsync(saved.Id, "AD/Replication", "category-admin");
        var published = Assert.Single(await fixture.Registry.ListPublishedAsync());
        Assert.Equal("AD/Replication", published.Category);
        Assert.Equal(before.Kql, published.Kql);
        Assert.Equal(before.Name, published.Name);
        Assert.Equal(SqlAuthenticationRoles.Admin, published.MinimumRole);
        Assert.Equal(before.Version, published.Version);
        Assert.Equal(before.PublishedVersionId, published.PublishedVersionId);
        var current = Assert.Single(await fixture.Registry.ListAsync());
        Assert.Equal(AnalysisQueryStatuses.Draft, current.Status);
        Assert.Equal("Unfinished KQL draft", current.Kql);
        Assert.Equal("category-admin", current.UpdatedBy);
        Assert.Single(current.Versions);
        Assert.Equal(before.Category, JsonSerializer.Deserialize<AnalysisQuerySnapshot>(
            current.Versions.Single().SnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Category);
        using var zip = new ZipArchive(new MemoryStream(await new AnalysisQueryExportService(fixture.Registry).ExportAsync()));
        using var stream = Assert.Single(zip.Entries).Open();
        using var json = await JsonDocument.ParseAsync(stream);
        Assert.Equal("AD/Replication", json.RootElement.GetProperty("category").GetString());
        await fixture.Registry.DisableAsync(saved.Id, "admin");
        await fixture.Categories.MoveQueryAsync(saved.Id, "AD", "admin");
        Assert.Empty(await fixture.Registry.ListPublishedAsync());
        Assert.False(Assert.Single(await fixture.Registry.ListAsync()).Enabled);
    }

    [Fact]
    public async Task RenamingAndReparentingMovesTheEntireSubtreeAtomically()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.SeedAsync("AD/Replication/Errors");
        await fixture.PublishAsync(saved.Id);
        await fixture.Categories.CreateAsync("Infrastructure", null);
        await fixture.Categories.UpdateAsync("ad/replication", "Health", "Infrastructure", "admin");
        Assert.Equal("Infrastructure/Health/Errors", Assert.Single(await fixture.Registry.ListPublishedAsync()).Category);
        Assert.DoesNotContain("AD/Replication", await fixture.Categories.ListAsync());
        Assert.Contains("AD", await fixture.Categories.ListAsync());
        await fixture.Categories.UpdateAsync("Infrastructure", "infra", null, "admin");
        Assert.Contains("infra/Health/Errors", await fixture.Categories.ListAsync());
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailCategoryRename BEFORE UPDATE OF Category ON AnalysisQueryDefinitions
                BEGIN SELECT RAISE(ABORT, 'Rename failed'); END;
                """);
        var paths = await fixture.Categories.ListAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Categories.UpdateAsync("infra/Health", "New", null, "admin"));
        Assert.Equal(paths, await fixture.Categories.ListAsync());
        Assert.Equal("infra/Health/Errors", Assert.Single(await fixture.Registry.ListPublishedAsync()).Category);
    }

    [Fact]
    public async Task RejectsCyclesCollisionsMissingTargetsAndNonemptyDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync("AD/Replication");
        await fixture.Categories.CreateAsync("Other", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.CreateAsync("ad", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.CreateAsync("Invalid/Name", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.CreateAsync("Child", "Missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.UpdateAsync("AD", "AD", "AD/Replication", "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.UpdateAsync("AD", "Other", null, "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.UpdateAsync("AD", new string('x', 128), null, "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.DeleteAsync("AD"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.DeleteAsync("AD/Replication"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.MoveQueryAsync(Guid.NewGuid(), "AD", "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Categories.MoveQueryAsync(Guid.NewGuid(), "Missing", "admin"));
        Assert.Equal(["AD", "AD/Replication", "Other"], await fixture.Categories.ListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/AD")]
    [InlineData("AD/")]
    [InlineData("AD//Replication")]
    [InlineData("AD/\nReplication")]
    public void InvalidPathsAreReportedByTheSharedValidator(string path)
    {
        var query = ActiveDirectoryAnalysisQueries.Create().First();
        query.Category = path;
        Assert.NotEmpty(AnalysisQueryValidator.ValidateDefinition(query));
    }

    [Fact]
    public async Task TreeUsesCaseInsensitivePathsAndDoesNotExposeExcludedQueryCategories()
    {
        AnalysisQueryDefinition[] queries =
        [
            new() { Name = "Root query", Category = "ad" },
            new() { Name = "<Safe>", Category = "AD/Replication/Errors" }
        ];
        var nodes = AnalysisQueryCategories.Build(queries);
        var root = Assert.Single(nodes);
        Assert.Equal(2, root.QueryCount);
        Assert.Single(root.Queries);
        Assert.Equal("Errors", Assert.Single(Assert.Single(root.Children).Children).Name);
        var library = AnalysisQueryCategories.Build(queries.Take(1));
        Assert.Empty(Assert.Single(library).Children);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisQueryCategoryTree>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Nodes"] = nodes,
                ["QueryListClass"] = "analysis-query-library-grid",
                ["QueryContent"] = (RenderFragment<AnalysisQueryDefinition>)(query => builder => builder.AddContent(0, query.Name))
            }))).ToHtmlString());
        Assert.Equal(3, html.Split("class=\"analysis-query-category\"").Length - 1);
        Assert.Contains("&lt;Safe&gt;", html);
        Assert.DoesNotContain("<Safe>", html);
        Assert.Equal(2, html.Split("class=\"analysis-query-library-grid\"").Length - 1);
    }

    [Fact]
    public async Task RegistryDropMovesTheSavedQueryAndPreservesUnsavedEditorContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.SeedAsync("AD");
        await fixture.Categories.CreateAsync("Replication", "AD");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new AdminAnalysisQueries();
        typeof(AdminAnalysisQueries).GetProperty("Registry", flags)!.SetValue(page, fixture.Registry);
        typeof(AdminAnalysisQueries).GetProperty("Categories", flags)!.SetValue(page, fixture.Categories);
        typeof(AdminAnalysisQueries).GetField("actor", flags)!.SetValue(page, "admin");
        var editor = new AnalysisQueryDefinition { Id = saved.Id, Category = "AD", Kql = "Unsaved editor text" };
        typeof(AdminAnalysisQueries).GetField("editing", flags)!.SetValue(page, editor);
        typeof(AdminAnalysisQueries).GetField("draggedQueryId", flags)!.SetValue(page, saved.Id);
        await (Task)typeof(AdminAnalysisQueries).GetMethod("DropQueryAsync", flags)!.Invoke(page, ["AD/Replication"])!;
        Assert.Null(typeof(AdminAnalysisQueries).GetField("errorMessage", flags)!.GetValue(page));
        Assert.Equal("AD/Replication", editor.Category);
        Assert.Equal("Unsaved editor text", editor.Kql);
        Assert.Equal("AD/Replication", Assert.Single(await fixture.Registry.ListAsync()).Category);
        await (Task)typeof(AdminAnalysisQueries).GetMethod("MoveQueryAsync", flags)!.Invoke(page, [saved.Id, "AD"])!;
        Assert.Equal("AD", Assert.Single(await fixture.Registry.ListAsync()).Category);
        editor.Category = "Unsaved/Choice";
        typeof(AdminAnalysisQueries).GetField("categoryName", flags)!.SetValue(page, "Other");
        await (Task)typeof(AdminAnalysisQueries).GetMethod("SaveCategoryAsync", flags)!.Invoke(page, [])!;
        Assert.Null(typeof(AdminAnalysisQueries).GetField("errorMessage", flags)!.GetValue(page));
        Assert.Equal("Unsaved/Choice", editor.Category);
        Assert.Equal("AD", Assert.Single(await fixture.Registry.ListAsync()).Category);
    }

    [Fact]
    public void MigrationAddsCategoryStorageWithoutChangingTheQuerySchema()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;").Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261021143000_AddAnalysisQueryScope", "20261021173000_AddAnalysisQueryCategories");
        Assert.Contains("CREATE TABLE [AnalysisQueryCategories]", script);
        Assert.Contains("SELECT DISTINCT [Category]", script);
        Assert.DoesNotContain("ALTER TABLE [AnalysisQueryDefinitions]", script);
        Assert.False(context.Database.HasPendingModelChanges());
        var provisioning = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        Assert.Contains("IF OBJECT_ID(N'[dbo].[AnalysisQueryCategories]', N'U') IS NULL", provisioning);
        Assert.Contains("20261021173000_AddAnalysisQueryCategories", provisioning);
    }

    private sealed class Fixture(SqliteConnection connection, ContextFactory factory) : IAsyncDisposable
    {
        public ContextFactory Factory { get; } = factory;
        public AnalysisQueryCategoryService Categories { get; } = new(factory);
        public AnalysisQueryRegistry Registry { get; } = CreateRegistry(factory);

        private static AnalysisQueryRegistry CreateRegistry(ContextFactory factory)
        {
            var options = new CrossTenantLogAnalyticsOptions { Workspaces = [new() { Key = "test" }] };
            return new(factory, options, new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options)));
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new ContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = factory.CreateDbContext();
            string[] tables = ["AnalysisQueryDefinitions", "AnalysisQueryWorkspaces", "AnalysisQueryVersions", "AnalysisQueryCategories"];
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => tables.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            return new(connection, factory);
        }

        public async Task<AnalysisQueryDefinition> SeedAsync(string category)
        {
            var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-fsmo-roles");
            query.Category = category;
            query.Workspaces.Add(new() { WorkspaceKey = "test" });
            return await Registry.SaveDraftAsync(query, "admin");
        }

        public async Task PublishAsync(Guid id)
        {
            await using var context = Factory.CreateDbContext();
            var query = await context.AnalysisQueryDefinitions.Include(query => query.Workspaces).SingleAsync(query => query.Id == id);
            query.Status = AnalysisQueryStatuses.Validated;
            foreach (var workspace in query.Workspaces) workspace.LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded;
            await context.SaveChangesAsync();
            await Registry.PublishAsync(id, "admin");
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class ContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Category management must not run Log Analytics queries.");
    }
}
