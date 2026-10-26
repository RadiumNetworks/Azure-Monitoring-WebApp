using System.Data;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed record OpenRcaNotice(
    Guid Id, string CategoryName, IReadOnlyList<string> Domains, string Status, string AssignedTo)
{
    public string StatusLabel => RcaStatuses.Label(Status);
    public string AssigneeLabel => string.IsNullOrWhiteSpace(AssignedTo) ? "Unassigned" : AssignedTo;
    public string Label => Domains.Count == 0
        ? $"Open RCA {CategoryName}"
        : $"Open RCA {CategoryName} {string.Join(", ", Domains)}";
}

public sealed class RcaService(
    IDbContextFactory<AlertDbContext> factory,
    IAuthorizationService authorization,
    CurrentUserIdentity identity,
    ApplicationAuthenticationOptions authentication,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<OpenRcaNotice>> GetOpenNoticesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var cases = await context.RootCauseAnalyses.AsNoTracking()
            .Where(rca => rca.Status != "Closed")
            .Select(rca => new { rca.Id, rca.CategoryName, rca.CreatedAt, rca.EvidenceEventIdsJson,
                rca.Status, rca.AssignedTo })
            .ToArrayAsync(cancellationToken);
        if (cases.Length == 0)
            return [];

        var evidence = cases.ToDictionary(rca => rca.Id, rca =>
            JsonSerializer.Deserialize<Guid[]>(rca.EvidenceEventIdsJson)
                ?? throw new InvalidOperationException($"RCA {rca.Id} has invalid evidence event IDs."));
        var eventIds = evidence.Values.SelectMany(ids => ids).Distinct().ToArray();
        var domains = await context.ParsedAlerts.AsNoTracking()
            .Where(alert => eventIds.Contains(alert.Id) && alert.InventoryComputerEntry != null)
            .Select(alert => new { alert.Id, alert.InventoryComputerEntry!.Domain })
            .ToDictionaryAsync(alert => alert.Id, alert => alert.Domain, cancellationToken);

        return cases.OrderByDescending(rca => rca.CreatedAt).ThenBy(rca => rca.Id)
            .Select(rca => new OpenRcaNotice(rca.Id, rca.CategoryName,
                evidence[rca.Id].Select(id => domains.GetValueOrDefault(id))
                    .OfType<string>().Select(domain => domain.Trim()).Where(domain => domain.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                rca.Status, rca.AssignedTo))
            .ToArray();
    }

    public async Task<IReadOnlyList<AlertRecord>> GetLinkedAlertsAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var alerts = await context.RcaAlertLinks.AsNoTracking().Where(link => link.RcaId == id)
            .Select(link => link.Alert).ToArrayAsync(cancellationToken);
        return alerts.OrderBy(alert => alert.ReceivedAt).ThenBy(alert => alert.Id).ToArray();
    }

    public async Task<IReadOnlyList<RootCauseAnalysis>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var cases = await context.RootCauseAnalyses.AsNoTracking().ToArrayAsync(cancellationToken);
        return cases.OrderByDescending(rca => rca.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<RcaPrompt>> GetPromptsAsync(Guid id)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await context.RcaPrompts.AsNoTracking().Where(prompt => prompt.RcaId == id)
            .OrderBy(prompt => prompt.Sequence).ToArrayAsync();
    }

    public async Task<IReadOnlyList<RcaChange>> GetChangesAsync(Guid id)
    {
        await using var context = await factory.CreateDbContextAsync();
        var changes = await context.RcaChanges.AsNoTracking().Where(change => change.RcaId == id).ToArrayAsync();
        return changes.OrderByDescending(change => change.CreatedAt).ThenBy(change => change.Field).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetAssigneesAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may list RCA assignees.");

        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.AuthenticationUsers.AsNoTracking()
            .Select(account => account.Username).ToArrayAsync(cancellationToken);
        var knownIdentities = await context.AnalysisDashboards.Select(dashboard => dashboard.OwnerKey)
            .Union(context.InboxLayouts.Select(layout => layout.OwnerKey))
            .Union(context.LogbookEntries.Select(entry => entry.User))
            .Union(context.AgentAnalysisJobs.Select(job => job.CreatedBy))
            .Union(context.AgentAnalysisReviewEvents.Select(review => review.Actor))
            .Union(context.AnalysisDashboardTemplates.Select(dashboard => dashboard.UpdatedBy))
            .Union(context.AnalysisQueryDefinitions.Select(query => query.UpdatedBy))
            .Union(context.AnalysisQueryVersions.Select(version => version.PublishedBy))
            .Union(context.RcaChanges.Select(change => change.Actor))
            .Union(context.RootCauseAnalyses.Select(rca => rca.AssignedTo))
            .ToArrayAsync(cancellationToken);
        var pastAssignments = await context.RcaChanges.Where(change => change.Field == "AssignedTo")
            .Select(change => new { change.Before, change.After }).ToArrayAsync(cancellationToken);
        var currentIdentity = identity.GetDisplayName(user);
        return accounts.Select(account => account.Trim())
            .Concat(knownIdentities.Concat(pastAssignments.SelectMany(change => new[] { change.Before, change.After }))
                .Append(currentIdentity ?? string.Empty)
                .Select(candidate => candidate.Trim()).Where(IsEmailAddress))
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task SaveAsync(
        ClaimsPrincipal user, Guid id, int revision, string analysis, string assignedTo,
        string status, string comment, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may edit RCAs.");
        if (!RcaStatuses.All.Contains(status))
            throw new InvalidOperationException("Unknown RCA status.");
        assignedTo = assignedTo.Trim();
        if (analysis.Length > 100_000 || comment.Length > 4000 || assignedTo.Length > 256)
            throw new InvalidOperationException("Analysis, comment or assignee exceeds the allowed length.");

        var actor = identity.GetActor(user, authentication.IsOpen);
        var now = timeProvider.GetUtcNow();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var rca = await context.RootCauseAnalyses.SingleAsync(rca => rca.Id == id, cancellationToken);
        if (rca.Revision != revision)
            throw new InvalidOperationException("This RCA changed. Refresh and reapply your changes.");
        if (assignedTo.Length > 0 && assignedTo != rca.AssignedTo && !IsEmailAddress(assignedTo) &&
            !await context.AuthenticationUsers.AnyAsync(
            candidate => candidate.Username == assignedTo, cancellationToken))
            throw new InvalidOperationException("Enter an email address or select an existing user as assignee.");

        var hasChanges = false;
        void Change(string field, string before, string after)
        {
            if (before == after)
                return;
            RcaAutomation.AddChange(context, rca, now, actor, field, before, after);
            hasChanges = true;
        }
        Change("Analysis", rca.Analysis, analysis);
        Change("AssignedTo", rca.AssignedTo, assignedTo);
        Change("Status", rca.Status, status);
        if (!string.IsNullOrWhiteSpace(comment))
        {
            RcaAutomation.AddChange(context, rca, now, actor, "Comment", "", comment.Trim());
            hasChanges = true;
        }
        if (!hasChanges)
            return;
        rca.Analysis = analysis;
        rca.AssignedTo = assignedTo;
        rca.Status = status;
        rca.Revision++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("This RCA changed. Refresh and reapply your changes.", exception);
        }
    }

    private static bool IsEmailAddress(string value) =>
        value.Length <= 256 && value.Contains('@') && !value.Any(char.IsControl) &&
        MailAddress.TryCreate(value, out var address) &&
        value.Equals(address.Address, StringComparison.OrdinalIgnoreCase);
}

public sealed class RcaWorker(
    IDbContextFactory<AlertDbContext> factory,
    AlertRuleEvaluator evaluator,
    QueryResultPresenter presenter,
    TimeProvider timeProvider,
    ILogger<RcaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        do
        {
            try
            {
                await using var strategyContext = await factory.CreateDbContextAsync(stoppingToken);
                await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var context = await factory.CreateDbContextAsync(stoppingToken);
                    await using var transaction = await context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable, stoppingToken);
                    await RcaAutomation.ReconcileAsync(context, evaluator, presenter,
                        timeProvider.GetUtcNow(), stoppingToken);
                    await context.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RCA reconciliation failed. Pending snapshots will be retried in 30 seconds.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
