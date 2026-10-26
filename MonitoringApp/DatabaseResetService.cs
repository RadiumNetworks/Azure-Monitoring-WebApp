using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class DatabaseResetService(
    IDbContextFactory<AlertDbContext> contextFactory,
    IAuthorizationService authorization,
    CurrentUserIdentity identity,
    ApplicationAuthenticationOptions authentication,
    DatabaseConfigurationStatus configuration,
    AlertStore alertStore,
    ILogger<DatabaseResetService> logger)
{
    public async Task<int> ResetAsync(
        ClaimsPrincipal user,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may reset the database.");
        if (!string.Equals(confirmation, "RESET", StringComparison.Ordinal))
            throw new InvalidOperationException("Type RESET exactly to confirm the permanent database reset.");
        if (!configuration.IsValid)
            throw new InvalidOperationException("The database is not configured. Check the application settings.");

        var actor = identity.GetActor(user, authentication.IsOpen);
        logger.LogWarning(
            "Database reset requested by {Actor}. Server={Server}, Database={Database}. Users and settings will be preserved.",
            actor, configuration.Connection.DataSource, configuration.Connection.InitialCatalog);

        int ruleCount;
        try
        {
            await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            ruleCount = await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);

                await context.RcaAlertLinks.ExecuteDeleteAsync(cancellationToken);
                await context.RcaChanges.ExecuteDeleteAsync(cancellationToken);
                await context.RcaPrompts.ExecuteDeleteAsync(cancellationToken);
                await context.RootCauseAnalyses.ExecuteDeleteAsync(cancellationToken);
                await context.AgentAnalysisReviewEvents.ExecuteDeleteAsync(cancellationToken);
                await context.AgentAnalysisJobs.ExecuteDeleteAsync(cancellationToken);
                await context.AnalysisDashboardTemplates.ExecuteDeleteAsync(cancellationToken);
                await context.AnalysisDashboards.ExecuteDeleteAsync(cancellationToken);
                await context.InboxLayouts.ExecuteDeleteAsync(cancellationToken);
                await context.AnalysisQueryWorkspaces.ExecuteDeleteAsync(cancellationToken);
                await context.AnalysisQueryVersions.ExecuteDeleteAsync(cancellationToken);
                await context.AnalysisQueryDefinitions.ExecuteDeleteAsync(cancellationToken);
                await context.AnalysisQueryCategories.ExecuteDeleteAsync(cancellationToken);
                await context.LogbookEntries.ExecuteDeleteAsync(cancellationToken);
                await context.ParsedAlerts.ExecuteDeleteAsync(cancellationToken);
                await context.AlertHistory.ExecuteDeleteAsync(cancellationToken);
                await context.Alerts.ExecuteDeleteAsync(cancellationToken);
                await context.ComputerInventory.ExecuteDeleteAsync(cancellationToken);
                await context.AlertRules.ExecuteDeleteAsync(cancellationToken);

                var rules = DefaultAlertRules.Create();
                context.AlertRules.AddRange(rules);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return rules.Count;
            });
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            logger.LogError(exception, "Database reset failed for {Actor}.", actor);
            throw new InvalidOperationException(
                "The database reset failed. Check the server logs and database state before trying again.", exception);
        }

        logger.LogWarning(
            "Database reset completed by {Actor}. Restored {RuleCount} default alert rules. Users and settings were preserved.",
            actor, ruleCount);
        alertStore.NotifyDataChanged();
        return ruleCount;
    }
}
