using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace MonitoringApp.Tests;

public sealed class DatabaseTransactionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitsSavedChangesAndPreservesIsolationAndResult(bool explicitIsolation)
    {
        await using var database = await TestDatabase.CreateAsync();
        var result = await DatabaseTransactions.ExecuteAsync(database, async context =>
        {
            Assert.Equal(IsolationLevel.Serializable, context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel);
            context.Settings.Add(new() { Name = "saved", JsonValue = "value" });
            await context.SaveChangesAsync();
            return 42;
        }, isolationLevel: explicitIsolation ? IsolationLevel.Serializable : null);
        Assert.Equal(42, result);
        await using var verify = database.CreateDbContext();
        Assert.Equal("value", (await verify.Settings.SingleAsync()).JsonValue);
    }

    [Fact]
    public async Task DoesNotImplicitlySaveTrackedChanges()
    {
        await using var database = await TestDatabase.CreateAsync();
        await DatabaseTransactions.ExecuteAsync(database, context =>
        {
            context.Settings.Add(new() { Name = "not-saved", JsonValue = "{}" });
            return Task.CompletedTask;
        });
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Settings.ToArrayAsync());
    }

    [Fact]
    public async Task FailureRollsBackMultipleSavesAndBulkWrites()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTransactions.ExecuteAsync(database, async context =>
        {
            context.Settings.Add(new() { Name = "first", JsonValue = "original" });
            await context.SaveChangesAsync();
            await context.Settings.ExecuteUpdateAsync(update => update.SetProperty(setting => setting.JsonValue, "changed"));
            context.Settings.Add(new() { Name = "second", JsonValue = "{}" });
            await context.SaveChangesAsync();
            throw new InvalidOperationException("Abort entire transaction.");
        }));
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Settings.ToArrayAsync());
    }

    [Fact]
    public async Task DeclinedResultRollsBackSavedChangesWithoutMaskingTheResult()
    {
        await using var database = await TestDatabase.CreateAsync();
        var result = await DatabaseTransactions.ExecuteAsync(database, async context =>
        {
            context.Settings.Add(new() { Name = "declined", JsonValue = "{}" });
            await context.SaveChangesAsync();
            return false;
        }, commitWhen: accepted => accepted);
        Assert.False(result);
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Settings.ToArrayAsync());
    }

    [Fact]
    public async Task RetryUsesFreshDisposedContextsAndRollsBackFailedAttempt()
    {
        await using var database = await TestDatabase.CreateAsync(retry: true);
        var attempts = new List<AlertDbContext>();
        await DatabaseTransactions.ExecuteAsync(database, async context =>
        {
            attempts.Add(context);
            Assert.Empty(context.ChangeTracker.Entries());
            context.Settings.Add(new() { Name = $"attempt-{attempts.Count}", JsonValue = "{}" });
            await context.SaveChangesAsync();
            if (attempts.Count == 1)
                throw new SimulatedTransientException();
        });
        Assert.Equal(2, attempts.Count);
        Assert.NotSame(attempts[0], attempts[1]);
        foreach (var context in attempts)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Settings.CountAsync());
        await using var verify = database.CreateDbContext();
        Assert.Equal("attempt-2", (await verify.Settings.SingleAsync()).Name);
    }

    [Fact]
    public async Task NonTransientFailureIsNotRetried()
    {
        await using var database = await TestDatabase.CreateAsync(retry: true);
        var attempts = 0;
        var failure = new InvalidOperationException("Not transient.");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseTransactions.ExecuteAsync(database, context =>
            {
                attempts++;
                throw failure;
            }));
        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationAfterSaveRollsBackAndPropagates()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseTransactions.ExecuteAsync(database, async context =>
        {
            context.Settings.Add(new() { Name = "cancelled", JsonValue = "{}" });
            await context.SaveChangesAsync(cancellation.Token);
            cancellation.Cancel();
        }, cancellation.Token));
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Settings.ToArrayAsync());
    }

    [Fact]
    public async Task PreCancelledOperationDoesNotRun()
    {
        await using var database = await TestDatabase.CreateAsync();
        var called = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseTransactions.ExecuteAsync(database, context =>
        {
            called = true;
            return Task.CompletedTask;
        }, new CancellationToken(canceled: true)));
        Assert.False(called);
    }

    private sealed class SimulatedTransientException : Exception;

    public sealed class RetryStrategyFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new RetryStrategy(dependencies);
    }

    private sealed class RetryStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SimulatedTransientException;
    }

    internal sealed class TestDatabase(SqliteConnection connection, DbContextOptions<AlertDbContext> options)
        : IDbContextFactory<AlertDbContext>, IAsyncDisposable
    {
        public AlertDbContext CreateDbContext() => new(options);

        public Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }

        public static async Task<TestDatabase> CreateAsync(bool retry = false, IInterceptor? interceptor = null,
            bool operationalTables = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection);
            if (operationalTables)
                options.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer,
                    AlertInventoryLearningTests.InventoryLearningTestModelCustomizer>();
            if (retry)
                options.ReplaceService<IExecutionStrategyFactory, RetryStrategyFactory>();
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            var database = new TestDatabase(connection, options.Options);
            await using var context = database.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => operationalTables || new[] { "Settings", "AuthenticationUsers" }.Any(name =>
                    statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            return database;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
