using System.Data;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace MonitoringApp.Tests;

public sealed class AnalysisQueryValidationTests
{
    [Fact]
    public async Task ValidationDisposesReadScopesBeforeSecretAndSdkCallsAndCommitsSerializable()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Registry.ValidateAsync(fixture.Id, "validator");

        Assert.Equal(AnalysisQueryStatuses.Validated, result.Status);
        Assert.Equal("validator", result.UpdatedBy);
        Assert.Equal(2, fixture.Query.Calls);
        Assert.All(result.Workspaces, workspace =>
        {
            Assert.Equal(AnalysisQueryValidationStatuses.Succeeded, workspace.LastValidationStatus);
            Assert.Equal("Validated with 1 row(s).", workspace.LastValidationMessage);
            Assert.NotNull(workspace.LastValidatedAt);
        });
        Assert.True(fixture.Interceptor.SawSerializableCommit);
        fixture.Factory.AssertDisposed();
        await fixture.Registry.PublishAsync(fixture.Id, "publisher");
        Assert.Equal(1, Assert.Single(await fixture.Registry.ListPublishedAsync()).Version);
    }

    [Theory]
    [InlineData("kql")]
    [InlineData("visualization")]
    [InlineData("limits")]
    [InlineData("workspace-enabled")]
    [InlineData("workspace-added")]
    [InlineData("workspace-removed")]
    [InlineData("workspace-validation")]
    [InlineData("rowversion")]
    [InlineData("publication")]
    [InlineData("deleted")]
    public async Task ConcurrentChangesArePreservedAndStaleResultsCannotApproveTheQuery(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        string? changedState = null;
        fixture.Query.BeforeResult = async () =>
        {
            fixture.Query.BeforeResult = null;
            await using (var context = fixture.Factory.CreateDbContext())
            {
                var stored = await context.AnalysisQueryDefinitions.Include(query => query.Workspaces).SingleAsync();
                var second = stored.Workspaces.Single(workspace => workspace.WorkspaceKey == "second");
                switch (change)
                {
                    case "kql": stored.Kql += "\n// changed"; break;
                    case "visualization": stored.VisualizationType = AnalysisQueryVisualizationTypes.Stat; break;
                    case "limits": stored.MaxTimeRangeMinutes = 43200; break;
                    case "workspace-enabled": second.Enabled = false; break;
                    case "workspace-added":
                        stored.Workspaces.Add(new() { WorkspaceKey = "another", Enabled = false });
                        break;
                    case "workspace-removed": context.AnalysisQueryWorkspaces.Remove(second); break;
                    case "workspace-validation":
                        second.LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded;
                        second.LastValidationMessage = "Another validation";
                        second.LastValidatedAt = DateTimeOffset.UtcNow;
                        break;
                    case "rowversion":
                        await context.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE \"AnalysisQueryDefinitions\" SET \"RowVersion\" = {new byte[] { 1, 2 }} WHERE \"Id\" = {fixture.Id}");
                        break;
                    case "publication":
                        stored.Version++;
                        stored.Status = AnalysisQueryStatuses.Published;
                        break;
                    case "deleted": context.AnalysisQueryDefinitions.Remove(stored); break;
                    default: throw new ArgumentOutOfRangeException(nameof(change));
                }
                await context.SaveChangesAsync();
            }
            changedState = await fixture.StateAsync();
        };

        var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            fixture.Registry.ValidateAsync(fixture.Id, "validator"));

        Assert.Contains("changed during validation", error.Message);
        Assert.Equal(changedState, await fixture.StateAsync());
        Assert.Equal(2, fixture.Query.Calls);
        fixture.Factory.AssertDisposed();
    }

    [Theory]
    [InlineData(AnalysisQueryScopes.SelectedSystems)]
    [InlineData(AnalysisQueryScopes.Workspace)]
    public async Task MissingInventoryRemainsAVisibleFailureForBothInteractiveScopes(string scope)
    {
        await using var fixture = await Fixture.CreateAsync(inventory: false, scope: scope);

        var result = await fixture.Registry.ValidateAsync(fixture.Id, "validator");

        Assert.Equal(AnalysisQueryStatuses.Draft, result.Status);
        Assert.All(result.Workspaces, workspace =>
        {
            Assert.Equal(AnalysisQueryValidationStatuses.Failed, workspace.LastValidationStatus);
            Assert.Equal("No inventory system routes to this workspace.", workspace.LastValidationMessage);
        });
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Equal(0, fixture.Secret.Calls);
    }

    [Fact]
    public async Task MissingConfiguredAssignmentsAreAddedDisabledWithoutCausingASpuriousConflict()
    {
        await using var fixture = await Fixture.CreateAsync(assignBoth: false);

        var result = await fixture.Registry.ValidateAsync(fixture.Id, "validator");

        Assert.Equal(AnalysisQueryStatuses.Validated, result.Status);
        Assert.Equal(2, result.Workspaces.Count);
        var added = result.Workspaces.Single(workspace => workspace.WorkspaceKey == "second");
        Assert.False(added.Enabled);
        Assert.Equal(AnalysisQueryValidationStatuses.NotValidated, added.LastValidationStatus);
        Assert.Null(added.LastValidatedAt);
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task PartialFailurePersistsPerWorkspaceButDoesNotAllowPublication()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Query.FailSecond = true;

        var result = await fixture.Registry.ValidateAsync(fixture.Id, "validator");

        Assert.Equal(AnalysisQueryStatuses.Draft, result.Status);
        Assert.Equal(AnalysisQueryValidationStatuses.Succeeded,
            result.Workspaces.Single(workspace => workspace.WorkspaceKey == "first").LastValidationStatus);
        var failed = result.Workspaces.Single(workspace => workspace.WorkspaceKey == "second");
        Assert.Equal(AnalysisQueryValidationStatuses.Failed, failed.LastValidationStatus);
        Assert.Contains("simulated failure", failed.LastValidationMessage);
        Assert.True(failed.LastValidationMessage.Length <= 1000);
        await Assert.ThrowsAsync<AnalysisValidationException>(() => fixture.Registry.PublishAsync(fixture.Id, "publisher"));
    }

    [Fact]
    public async Task CancellationDiscardsAllPendingValidationResults()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.StateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Query.BeforeResult = () =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Registry.ValidateAsync(fixture.Id, "validator", cancellation.Token));

        Assert.Equal(original, await fixture.StateAsync());
        fixture.Factory.AssertDisposed();
    }

    [Fact]
    public async Task FailureAfterSaveRollsBackStatusAuditAndAllWorkspaceResults()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.StateAsync();
        fixture.Interceptor.FailAfterSave = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Registry.ValidateAsync(fixture.Id, "validator"));

        Assert.Equal("Simulated validation commit failure.", error.Message);
        Assert.Equal(original, await fixture.StateAsync());
        Assert.True(fixture.Interceptor.SawSerializableCommit);
        fixture.Interceptor.FailAfterSave = false;
        Assert.Equal(AnalysisQueryStatuses.Validated,
            (await fixture.Registry.ValidateAsync(fixture.Id, "validator")).Status);
    }

    private sealed class Fixture(SqliteConnection connection, Factory factory, CommitInterceptor interceptor) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public CommitInterceptor Interceptor { get; } = interceptor;
        public CrossTenantLogAnalyticsOptions Options { get; } = CrossTenantLogAnalyticsOptionsTests.CreateValid(
        [
            CrossTenantLogAnalyticsOptionsTests.Workspace("first", "11111111-1111-1111-1111-111111111111", "first.example"),
            CrossTenantLogAnalyticsOptionsTests.Workspace("second", "22222222-2222-2222-2222-222222222222", "second.example")
        ]);
        public Guid Id { get; private set; }
        public Secret Secret { get; } = new(factory);
        public Query Query { get; } = new(factory);
        public AnalysisQueryRegistry Registry => new(Factory, Options,
            new(Options, Secret, new TargetWorkspaceResolver(Options), Query));

        public static async Task<Fixture> CreateAsync(
            bool inventory = true, bool assignBoth = true, string scope = AnalysisQueryScopes.SelectedSystems)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new CommitInterceptor();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .AddInterceptors(interceptor).Options);
            var fixture = new Fixture(connection, factory, interceptor);
            await using (var context = factory.CreateDbContext())
            {
                string[] tables = ["AnalysisQueryDefinitions", "AnalysisQueryWorkspaces", "AnalysisQueryVersions", "ComputerInventory"];
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(statement => tables.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                        .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
                if (inventory)
                    context.ComputerInventory.AddRange(fixture.Options.Workspaces.Select((workspace, index) =>
                        new ComputerInventoryEntry { SubscriptionId = workspace.SubscriptionIds[0], Computer = $"DC0{index + 1}" }));
                await context.SaveChangesAsync();
            }
            var draft = ActiveDirectoryAnalysisQueries.Create().Single(query => query.Key == "ad-netlogon-errors");
            draft.Scope = scope;
            draft.MaxTimeRangeMinutes = 1440;
            if (scope == AnalysisQueryScopes.Workspace)
                draft.Kql = "Event | where TimeGenerated > ago(_TimePeriod) | take 1";
            draft.Workspaces = fixture.Options.Workspaces.Take(assignBoth ? 2 : 1)
                .Select(workspace => new AnalysisQueryWorkspace { WorkspaceKey = workspace.Key, Enabled = true }).ToArray();
            fixture.Id = (await fixture.Registry.SaveDraftAsync(draft, "seed")).Id;
            if (!assignBoth)
            {
                await using var context = factory.CreateDbContext();
                context.AnalysisQueryWorkspaces.Remove(
                    await context.AnalysisQueryWorkspaces.SingleAsync(workspace => workspace.WorkspaceKey == "second"));
                await context.SaveChangesAsync();
            }
            return fixture;
        }

        public async Task<string> StateAsync()
        {
            await using var context = Factory.CreateDbContext();
            var queries = await context.AnalysisQueryDefinitions.AsNoTracking().Include(query => query.Workspaces).ToArrayAsync();
            return JsonSerializer.Serialize(queries.Select(query => new
            {
                query.Id, query.Kql, query.VisualizationType, query.MaxTimeRangeMinutes, query.Status, query.Version,
                query.RowVersion, query.UpdatedAt, query.UpdatedBy,
                Workspaces = query.Workspaces.OrderBy(workspace => workspace.WorkspaceKey).Select(workspace => new
                {
                    workspace.WorkspaceKey, workspace.Enabled, workspace.LastValidatedAt,
                    workspace.LastValidationStatus, workspace.LastValidationMessage
                })
            }));
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        private readonly List<AlertDbContext> created = [];
        public AlertDbContext CreateDbContext()
        {
            var context = new AlertDbContext(options);
            created.Add(context);
            return context;
        }
        public void AssertDisposed()
        {
            foreach (var context in created)
                Assert.Throws<ObjectDisposedException>(() => { _ = context.ChangeTracker; });
        }
    }

    private sealed class Secret(Factory factory) : ILogAnalyticsSecretProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
        {
            factory.AssertDisposed();
            Calls++;
            return Task.FromResult("test-secret");
        }
    }

    private sealed class Query(Factory factory) : IAnalysisLogAnalyticsQuery
    {
        public int Calls { get; private set; }
        public bool FailSecond { get; set; }
        public Func<Task>? BeforeResult { get; set; }
        public async Task<LogsQueryResult> QueryAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
            string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            factory.AssertDisposed();
            Calls++;
            Assert.Equal(60, timeRangeMinutes);
            if (BeforeResult is not null) await BeforeResult();
            if (FailSecond && workspace.Key == "second")
                throw new InvalidOperationException("simulated failure " + new string('x', 2000));
            return ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(
                """{"tables":[{"name":"PrimaryResult","columns":[{"name":"Computer","type":"string"}],"rows":[["DC01"]]}]}"""))!;
        }
    }

    private sealed class CommitInterceptor : SaveChangesInterceptor
    {
        public bool FailAfterSave { get; set; }
        public bool SawSerializableCommit { get; private set; }
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            if (context.ChangeTracker.Entries<AnalysisQueryDefinition>().Any(entry => entry.Entity.UpdatedBy == "validator"))
            {
                SawSerializableCommit = context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel == IsolationLevel.Serializable;
                if (FailAfterSave)
                    throw new InvalidOperationException("Simulated validation commit failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
