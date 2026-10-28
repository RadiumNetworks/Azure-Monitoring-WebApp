using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class AnalysisQueryWorkspaceAssignmentTests
{
    [Fact]
    public async Task AssignsNewAndDisabledWorkspacesToAllQueriesWithoutPublishingOrOverwritingOtherSettings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var active = await fixture.SeedAsync("active");
        var disabled = await fixture.SeedAsync("disabled");
        await fixture.Registry.DisableAsync(disabled.Id, "previous-admin");
        fixture.AddWorkspace();

        var result = await fixture.Registry.EnableWorkspacesForAllAsync(["tenant-b", "tenant-c"], true, " bulk-admin ");

        Assert.Equal(new AnalysisQueryWorkspaceAssignmentResult(2, 4, 1), result);
        var queries = await fixture.Registry.ListAsync();
        Assert.Equal(2, queries.Count);
        foreach (var query in queries)
        {
            var original = query.Id == active.Id ? active : disabled;
            Assert.True(query.Enabled);
            Assert.Equal(AnalysisQueryStatuses.Draft, query.Status);
            Assert.Equal("bulk-admin", query.UpdatedBy);
            Assert.Equal(original.Kql, query.Kql);
            Assert.Equal(original.VisualizationJson, query.VisualizationJson);
            Assert.Equal(original.Category, query.Category);
            Assert.Equal(original.MinimumRole, query.MinimumRole);
            Assert.Equal(original.PublishedVersionId, query.PublishedVersionId);
            Assert.Equal(original.Version, query.Version);
            Assert.Equal(Assert.Single(original.Versions).SnapshotJson, Assert.Single(query.Versions).SnapshotJson);
            var existing = query.Workspaces.Single(item => item.WorkspaceKey == "tenant-a");
            Assert.True(existing.Enabled);
            Assert.Equal(AnalysisQueryValidationStatuses.Succeeded, existing.LastValidationStatus);
            Assert.Equal(Fixture.ValidatedAt, existing.LastValidatedAt);
            Assert.Equal("Previously validated", existing.LastValidationMessage);
            foreach (var key in new[] { "tenant-b", "tenant-c" })
            {
                var assignment = query.Workspaces.Single(item => item.WorkspaceKey == key);
                Assert.True(assignment.Enabled);
                Assert.Equal(AnalysisQueryValidationStatuses.NotValidated, assignment.LastValidationStatus);
                Assert.Null(assignment.LastValidatedAt);
                Assert.Empty(assignment.LastValidationMessage);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Registry.PublishAsync(query.Id, "admin"));
        }
        Assert.Equal(2, (await fixture.Registry.ListPublishedAsync()).Count);
    }

    [Fact]
    public async Task CanLeaveDisabledQueriesUntouched()
    {
        await using var fixture = await Fixture.CreateAsync();
        var active = await fixture.SeedAsync("active");
        var disabled = await fixture.SeedAsync("disabled");
        await fixture.Registry.DisableAsync(disabled.Id, "previous-admin");

        var result = await fixture.Registry.EnableWorkspacesForAllAsync(["tenant-b"], false, "admin");

        Assert.Equal(new AnalysisQueryWorkspaceAssignmentResult(1, 1, 0), result);
        var queries = await fixture.Registry.ListAsync();
        Assert.True(queries.Single(query => query.Id == active.Id).Workspaces.Single(item => item.WorkspaceKey == "tenant-b").Enabled);
        var unchanged = queries.Single(query => query.Id == disabled.Id);
        Assert.False(unchanged.Enabled);
        Assert.Equal(AnalysisQueryStatuses.Disabled, unchanged.Status);
        Assert.Equal("previous-admin", unchanged.UpdatedBy);
        Assert.False(unchanged.Workspaces.Single(item => item.WorkspaceKey == "tenant-b").Enabled);
        Assert.Equal(AnalysisQueryValidationStatuses.Failed, unchanged.Workspaces.Single(item => item.WorkspaceKey == "tenant-b").LastValidationStatus);
    }

    [Fact]
    public async Task NormalizesWorkspaceKeysAndRepeatedAssignmentsDoNotInvalidateExistingApprovals()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync("active");

        var unchanged = await fixture.Registry.EnableWorkspacesForAllAsync([" TENANT-A ", "tenant-a"], true, "other-admin");
        Assert.Equal(new AnalysisQueryWorkspaceAssignmentResult(0, 0, 0), unchanged);
        var original = Assert.Single(await fixture.Registry.ListAsync());
        Assert.Equal(AnalysisQueryStatuses.Published, original.Status);

        var first = await fixture.Registry.EnableWorkspacesForAllAsync([" TENANT-B ", "tenant-b"], true, "first-admin");
        Assert.Equal(new AnalysisQueryWorkspaceAssignmentResult(1, 1, 0), first);
        var changed = Assert.Single(await fixture.Registry.ListAsync());
        var second = await fixture.Registry.EnableWorkspacesForAllAsync(["tenant-b"], true, "second-admin");
        Assert.Equal(new AnalysisQueryWorkspaceAssignmentResult(0, 0, 0), second);
        var repeated = Assert.Single(await fixture.Registry.ListAsync());
        Assert.Equal(2, repeated.Workspaces.Count);
        Assert.Equal(changed.UpdatedAt, repeated.UpdatedAt);
        Assert.Equal("first-admin", repeated.UpdatedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("tenant-b,unknown")]
    public async Task RejectsInvalidWorkspaceSelectionsWithoutChangingAnyQueries(string keys)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync("active");
        var before = Assert.Single(await fixture.Registry.ListAsync());
        var selection = keys.Length == 0 ? [] : keys.Split(',');

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Registry.EnableWorkspacesForAllAsync(selection, true, "admin"));

        var after = Assert.Single(await fixture.Registry.ListAsync());
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(after.Workspaces.Single(item => item.WorkspaceKey == "tenant-b").Enabled);
    }

    [Fact]
    public async Task EditorGuardPreservesUnsavedChangesAndPageReportsBulkResults()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.SeedAsync("active");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new AdminAnalysisQueries();
        typeof(AdminAnalysisQueries).GetProperty("Registry", flags)!.SetValue(page, fixture.Registry);
        typeof(AdminAnalysisQueries).GetProperty("Categories", flags)!.SetValue(page, new AnalysisQueryCategoryService(fixture.Factory));
        typeof(AdminAnalysisQueries).GetField("actor", flags)!.SetValue(page, "admin");
        var selection = Assert.IsType<HashSet<string>>(typeof(AdminAnalysisQueries).GetField("bulkWorkspaceKeys", flags)!.GetValue(page));
        selection.Add("tenant-b");
        var editor = new AnalysisQueryDefinition { Id = saved.Id, Kql = "Unsaved editor text" };
        typeof(AdminAnalysisQueries).GetField("editing", flags)!.SetValue(page, editor);
        var apply = typeof(AdminAnalysisQueries).GetMethod("AssignWorkspacesToAllAsync", flags)!;

        await (Task)apply.Invoke(page, [])!;

        Assert.Contains("Close the query editor", Assert.IsType<string>(
            typeof(AdminAnalysisQueries).GetField("errorMessage", flags)!.GetValue(page)));
        Assert.Equal("Unsaved editor text", editor.Kql);
        Assert.False(Assert.Single(await fixture.Registry.ListAsync()).Workspaces.Single(item => item.WorkspaceKey == "tenant-b").Enabled);

        typeof(AdminAnalysisQueries).GetField("editing", flags)!.SetValue(page, null);
        await (Task)apply.Invoke(page, [])!;

        Assert.Null(typeof(AdminAnalysisQueries).GetField("errorMessage", flags)!.GetValue(page));
        Assert.Contains("enabled 1 workspace assignment(s)", Assert.IsType<string>(
            typeof(AdminAnalysisQueries).GetField("statusMessage", flags)!.GetValue(page)));
        Assert.True(Assert.Single(await fixture.Registry.ListAsync()).Workspaces.Single(item => item.WorkspaceKey == "tenant-b").Enabled);
    }

    private sealed class Fixture(SqliteConnection connection, ContextFactory factory) : IAsyncDisposable
    {
        public static readonly DateTimeOffset ValidatedAt = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        public ContextFactory Factory { get; } = factory;
        public CrossTenantLogAnalyticsOptions Options { get; private set; } = new()
        {
            Workspaces = [new() { Key = "tenant-a" }, new() { Key = "tenant-b" }]
        };
        public AnalysisQueryRegistry Registry => new(Factory, Options,
            new AnalysisQueryRunner(Options, new UnusedSecretProvider(), new TargetWorkspaceResolver(Options)));

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

        public async Task<AnalysisQueryDefinition> SeedAsync(string key)
        {
            var query = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-fsmo-roles");
            query.Key = key;
            query.Workspaces = [new() { WorkspaceKey = "tenant-a" }, new() { WorkspaceKey = "tenant-b", Enabled = false }];
            var saved = await Registry.SaveDraftAsync(query, "admin");
            await using (var context = Factory.CreateDbContext())
            {
                var stored = await context.AnalysisQueryDefinitions.Include(item => item.Workspaces).SingleAsync(item => item.Id == saved.Id);
                stored.Status = AnalysisQueryStatuses.Validated;
                foreach (var assignment in stored.Workspaces)
                {
                    assignment.LastValidatedAt = ValidatedAt;
                    assignment.LastValidationStatus = assignment.Enabled ? AnalysisQueryValidationStatuses.Succeeded : AnalysisQueryValidationStatuses.Failed;
                    assignment.LastValidationMessage = assignment.Enabled ? "Previously validated" : "Old failure";
                }
                await context.SaveChangesAsync();
            }
            await Registry.PublishAsync(saved.Id, "admin");
            return (await Registry.ListAsync()).Single(item => item.Id == saved.Id);
        }

        public void AddWorkspace() =>
            Options = new() { Workspaces = [.. Options.Workspaces, new() { Key = "tenant-c" }] };

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class ContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bulk assignments must not access Key Vault or execute queries.");
    }
}
