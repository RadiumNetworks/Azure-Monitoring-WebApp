using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public static class DatabaseTransactions
{
    public static Task ExecuteAsync(
        IDbContextFactory<AlertDbContext> factory,
        Func<AlertDbContext, Task> operation,
        CancellationToken cancellationToken = default,
        IsolationLevel? isolationLevel = null) =>
        ExecuteAsync(factory, async context =>
        {
            await operation(context);
            return true;
        }, cancellationToken, isolationLevel);

    /// <summary>
    /// Runs each retry with a fresh context and transaction. The caller owns SaveChanges
    /// and must keep external side effects outside the operation. A false commit predicate rolls back.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        IDbContextFactory<AlertDbContext> factory,
        Func<AlertDbContext, Task<T>> operation,
        CancellationToken cancellationToken = default,
        IsolationLevel? isolationLevel = null,
        Func<T, bool>? commitWhen = null)
    {
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async executionToken =>
        {
            await using var context = await factory.CreateDbContextAsync(executionToken);
            await using var transaction = isolationLevel is { } level
                ? await context.Database.BeginTransactionAsync(level, executionToken)
                : await context.Database.BeginTransactionAsync(executionToken);
            var result = await operation(context);
            if (commitWhen is null || commitWhen(result))
                await transaction.CommitAsync(executionToken);
            else
                await transaction.RollbackAsync(executionToken);
            return result;
        }, cancellationToken);
    }
}
