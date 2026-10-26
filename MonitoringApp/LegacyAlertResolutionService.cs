using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class LegacyAlertResolutionService(
    IDbContextFactory<AlertDbContext> factory,
    IAuthorizationService authorization,
    CurrentUserIdentity identity,
    ApplicationAuthenticationOptions authentication,
    DatabaseConfigurationStatus configuration,
    AlertStore alertStore,
    AlertRuleEvaluator evaluator,
    QueryResultPresenter presenter,
    TimeProvider timeProvider,
    ILogger<LegacyAlertResolutionService> logger)
{
    public async Task<int> CountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(user);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return (await GetCandidatesAsync(context, cancellationToken)).Count;
    }

    public async Task<int> ResolveAsync(
        ClaimsPrincipal user, string confirmation, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(user);
        if (confirmation != "RESOLVE")
            throw new InvalidOperationException("Type RESOLVE exactly to confirm legacy alert resolution.");
        var actor = identity.GetActor(user, authentication.IsOpen);
        var now = timeProvider.GetUtcNow();
        int resolvedCount;
        try
        {
            await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
            resolvedCount = await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var context = await factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                var candidates = await GetCandidatesAsync(context, cancellationToken);
                if (candidates.Count == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return 0;
                }
                var updates = candidates.Select(alert => new
                {
                    Alert = alert,
                    Comments = string.IsNullOrWhiteSpace(alert.Comments)
                        ? "Resolved"
                        : $"{alert.Comments.TrimEnd()}\nResolved"
                }).ToArray();
                var tooLong = updates.FirstOrDefault(update => update.Comments.Length > 4000);
                if (tooLong is not null)
                    throw new InvalidOperationException(
                        $"Alert {tooLong.Alert.Id} has no room for the Resolved marker. Shorten its comments and retry. No alerts were changed.");

                var candidateIds = candidates.Select(alert => alert.Id).ToHashSet();
                var parsedIds = (await context.ParsedAlerts.Select(alert => alert.Id)
                    .ToArrayAsync(cancellationToken)).ToHashSet();
                foreach (var update in updates)
                {
                    var changed = await context.Alerts.Where(alert => alert.Id == update.Alert.Id).ExecuteUpdateAsync(
                        setters => setters.SetProperty(alert => alert.Comments, update.Comments), cancellationToken);
                    if (changed != 1)
                        throw new DbUpdateConcurrencyException("A legacy alert changed during resolution. Refresh and retry.");
                    context.LogbookEntries.Add(new()
                    {
                        Id = Guid.NewGuid(), CreatedAt = now, User = actor, AlertRecordId = update.Alert.Id,
                        Comment = $"Resolved legacy alert without an inventory assignment.\nAlert: {update.Alert.Name}\n" +
                            $"Target: {update.Alert.TargetName}\nSubscription: {update.Alert.SubscriptionId}"
                    });
                    if (!parsedIds.Contains(update.Alert.Id))
                    {
                        var parsed = ParsedAlertFactory.Create(update.Alert);
                        parsed.InventorySubscriptionId = null;
                        parsed.InventoryComputer = null;
                        context.ParsedAlerts.Add(parsed);
                    }
                }
                await context.SaveChangesAsync(cancellationToken);
                var records = await context.ParsedAlerts.Include(record => record.Alert)
                    .ToArrayAsync(cancellationToken);
                foreach (var record in records.Where(record => candidateIds.Contains(record.Id)))
                    record.ResolvedAt = now;
                var rules = await context.AlertRules.AsNoTracking().ToArrayAsync(cancellationToken);
                var inventory = await context.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken);
                ParsedAlertLifecycle.Synchronize(records, rules, evaluator, inventory);
                await context.SaveChangesAsync(cancellationToken);
                await RcaAutomation.ReconcileAsync(context, evaluator, presenter, now, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return candidates.Count;
            });
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException)
        {
            logger.LogError(exception, "Legacy alert resolution failed for {Actor}.", actor);
            throw;
        }
        logger.LogWarning("Resolved {AlertCount} legacy alerts without inventory assignments by {Actor}.", resolvedCount, actor);
        if (resolvedCount > 0)
            alertStore.NotifyDataChanged();
        return resolvedCount;
    }

    private async Task EnsureAdminAsync(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may resolve legacy alerts.");
        if (!configuration.IsValid)
            throw new InvalidOperationException("The database is not configured. Check the application settings.");
    }

    private static async Task<IReadOnlyList<AlertRecord>> GetCandidatesAsync(
        AlertDbContext context, CancellationToken cancellationToken)
    {
        var alerts = await context.Alerts.AsNoTracking().ToArrayAsync(cancellationToken);
        var assignedIds = (await context.ParsedAlerts.Where(alert => alert.InventoryComputerEntry != null)
            .Select(alert => alert.Id).ToArrayAsync(cancellationToken)).ToHashSet();
        return AlertLifecycle.GetActiveAlerts(alerts).Where(alert => !assignedIds.Contains(alert.Id)).ToArray();
    }
}
