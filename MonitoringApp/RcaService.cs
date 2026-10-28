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

public sealed record RcaAlertSelection(Guid AlertEventId, Guid? ExpectedRcaId, int? ExpectedRcaRevision);

public sealed record RcaAlertCandidate(AlertRecord Alert, bool IsActive, Guid? RcaId, string? RcaName, int? RcaRevision)
{
    public RcaAlertSelection Selection => new(Alert.Id, RcaId, RcaRevision);
}

public sealed class RcaService(
    IDbContextFactory<AlertDbContext> factory,
    IAuthorizationService authorization,
    CurrentUserIdentity identity,
    ApplicationAuthenticationOptions authentication,
    TimeProvider timeProvider,
    AlertRuleEvaluator evaluator,
    QueryResultPresenter presenter)
{
    public async Task<IReadOnlyList<RcaAlertCandidate>> GetAssignableAlertsAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await RequireAdministratorAsync(user);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return (await LoadAssignmentsAsync(context, timeProvider.GetUtcNow(), cancellationToken)).Candidates;
    }

    public Task<Guid> CreateAsync(
        ClaimsPrincipal user, string name, IReadOnlyList<RcaAlertSelection> alerts,
        bool confirmReassignment, CancellationToken cancellationToken = default) =>
        AssignAsync(user, null, null, name, alerts, confirmReassignment, cancellationToken);

    public async Task AssignAlertsAsync(
        ClaimsPrincipal user, Guid id, int revision, IReadOnlyList<RcaAlertSelection> alerts,
        bool confirmReassignment, CancellationToken cancellationToken = default) =>
        await AssignAsync(user, id, revision, null, alerts, confirmReassignment, cancellationToken);

    private async Task<Guid> AssignAsync(
        ClaimsPrincipal user, Guid? id, int? revision, string? name,
        IReadOnlyList<RcaAlertSelection> selections, bool confirmReassignment, CancellationToken cancellationToken)
    {
        await RequireAdministratorAsync(user);
        name = name?.Trim();
        if (id is null && (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)))
            throw new InvalidOperationException("Enter an RCA name of 1-256 characters without control characters.");
        if (selections.Count == 0 || selections.Select(selection => selection.AlertEventId).Distinct().Count() != selections.Count)
            throw new InvalidOperationException("Select at least one alert, without duplicate selections.");
        var actor = identity.GetActor(user, authentication.IsOpen);
        var targetId = id ?? Guid.NewGuid();
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        try
        {
            await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var context = await factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                var now = timeProvider.GetUtcNow();
                var state = await LoadAssignmentsAsync(context, now, cancellationToken);
                var target = id is null ? new RootCauseAnalysis
                {
                    Id = targetId, CategoryName = name!, RuleId = Guid.Empty,
                    CreatedAt = now, FollowUpDueAt = now.AddMinutes(15), Revision = 1
                } : state.Cases.SingleOrDefault(rca => rca.Id == id)
                    ?? throw new InvalidOperationException("The selected RCA no longer exists.");
                if (id is not null && target.Revision != revision)
                    throw new InvalidOperationException("This RCA changed. Refresh and reapply your changes.");
                if (target.Status == RcaStatuses.Closed)
                    throw new InvalidOperationException("Reopen the RCA before assigning alerts.");

                var candidates = state.Candidates.ToDictionary(candidate => candidate.Alert.Id);
                var selected = selections.Select(selection =>
                {
                    if (!candidates.TryGetValue(selection.AlertEventId, out var candidate))
                        throw new InvalidOperationException("A selected alert is no longer active or within the last 24 hours. Refresh the alert list.");
                    if (candidate.RcaId != selection.ExpectedRcaId || candidate.RcaRevision != selection.ExpectedRcaRevision)
                        throw new InvalidOperationException("An alert's RCA assignment changed. Refresh the alert list and confirm the current assignment.");
                    if (candidate.RcaId is not null && candidate.RcaId != targetId && !confirmReassignment)
                        throw new InvalidOperationException("Confirm moving alerts from their existing RCA before saving.");
                    return candidate;
                }).ToArray();
                var lifecycleKeys = selected.Select(candidate => RcaAutomation.LifecycleKey(candidate.Alert)).ToHashSet();
                var related = state.Alerts.Where(alert => lifecycleKeys.Contains(RcaAutomation.LifecycleKey(alert))).ToArray();
                var eventIds = related.Select(alert => alert.Id).ToHashSet();
                if (id is null)
                {
                    context.RootCauseAnalyses.Add(target);
                    RcaAutomation.AddChange(context, target, now, actor, "Created", "", name!);
                }
                foreach (var source in state.Cases.Where(rca => rca.Id != targetId))
                {
                    var evidence = ReadEvidence(source);
                    var remaining = evidence.Except(eventIds).Order().ToArray();
                    var movedLinks = state.Links.Where(link => link.RcaId == source.Id && eventIds.Contains(link.AlertEventId)).ToArray();
                    if (!evidence.Any(eventIds.Contains) && movedLinks.Length == 0)
                        continue;
                    source.EvidenceEventIdsJson = JsonSerializer.Serialize(remaining);
                    source.Revision++;
                    RcaAutomation.AddChange(context, source, now, actor, "AlertReassignment",
                        JsonSerializer.Serialize(evidence.Intersect(eventIds).Union(movedLinks.Select(link => link.AlertEventId)).Order()),
                        $"Moved to RCA {targetId} ({target.CategoryName}).");
                }
                foreach (var alert in related)
                {
                    var link = state.Links.SingleOrDefault(link => link.AlertEventId == alert.Id);
                    if (link is null)
                        context.RcaAlertLinks.Add(new() { AlertEventId = alert.Id, RcaId = targetId, LinkedAt = now });
                    else if (link.RcaId != targetId)
                    {
                        link.RcaId = targetId;
                        link.LinkedAt = now;
                    }
                }
                var before = target.EvidenceEventIdsJson;
                target.EvidenceEventIdsJson = JsonSerializer.Serialize(ReadEvidence(target).Union(eventIds).Order());
                target.Revision++;
                RcaAutomation.AddChange(context, target, now, actor, "AlertAssignment", before,
                    JsonSerializer.Serialize(new
                    {
                        Alerts = selected.Select(candidate => new
                        {
                            candidate.Alert.Id, PreviousRcaId = candidate.RcaId, PreviousRcaName = candidate.RcaName
                        }),
                        AssignedEventIds = eventIds.Order()
                    }));
                await context.SaveChangesAsync(cancellationToken);
                await RcaAutomation.ReconcileAsync(context, evaluator, presenter, now, cancellationToken);
                if (id is null)
                {
                    var evidenceIds = ReadEvidence(target).ToHashSet();
                    var evidence = state.Alerts.Where(alert => evidenceIds.Contains(alert.Id))
                        .OrderBy(alert => alert.ReceivedAt).ToArray();
                    RcaAutomation.CapturePrompt(context, target, evidence, null, now, 0, presenter);
                }
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("An RCA changed. Refresh and reapply your changes.", exception);
        }
        return targetId;
    }

    private async Task RequireAdministratorAsync(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may create RCAs or assign alerts.");
    }

    private static Guid[] ReadEvidence(RootCauseAnalysis rca) =>
        JsonSerializer.Deserialize<Guid[]>(rca.EvidenceEventIdsJson)
            ?? throw new InvalidOperationException($"RCA {rca.Id} has invalid evidence event IDs.");

    private static async Task<(AlertRecord[] Alerts, RootCauseAnalysis[] Cases, RcaAlertLink[] Links, RcaAlertCandidate[] Candidates)> LoadAssignmentsAsync(
        AlertDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await context.Alerts.AsNoTracking().ToArrayAsync(cancellationToken);
        var alerts = stored.Where(alert => !InventoryHeartbeatBatch.IsInventoryHeartbeat(alert)).ToArray();
        var cases = await context.RootCauseAnalyses.ToArrayAsync(cancellationToken);
        var links = await context.RcaAlertLinks.ToArrayAsync(cancellationToken);
        var alertsById = alerts.ToDictionary(alert => alert.Id);
        var casesById = cases.ToDictionary(rca => rca.Id);
        var owners = new Dictionary<string, RootCauseAnalysis>();
        foreach (var link in links)
        {
            if (!alertsById.TryGetValue(link.AlertEventId, out var alert))
                continue;
            var key = RcaAutomation.LifecycleKey(alert);
            if (owners.TryGetValue(key, out var owner) && owner.Id != link.RcaId)
                throw new InvalidOperationException($"Alert lifecycle {alert.Id} belongs to conflicting RCAs.");
            owners[key] = casesById[link.RcaId];
        }
        foreach (var rca in cases.OrderBy(rca => rca.CreatedAt).ThenBy(rca => rca.Id))
            foreach (var eventId in ReadEvidence(rca))
                if (alertsById.TryGetValue(eventId, out var alert))
                    owners.TryAdd(RcaAutomation.LifecycleKey(alert), rca);
        var activeIds = AlertLifecycle.GetActiveAlerts(alerts).Select(alert => alert.Id).ToHashSet();
        var cutoff = now.AddHours(-24);
        var candidates = alerts.GroupBy(RcaAutomation.LifecycleKey)
            .Select(group => group.OrderByDescending(alert => alert.ReceivedAt).First())
            .Where(alert => activeIds.Contains(alert.Id) || alert.ReceivedAt >= cutoff)
            .OrderByDescending(alert => alert.ReceivedAt)
            .Select(alert =>
            {
                owners.TryGetValue(RcaAutomation.LifecycleKey(alert), out var owner);
                return new RcaAlertCandidate(alert, activeIds.Contains(alert.Id), owner?.Id, owner?.CategoryName, owner?.Revision);
            }).ToArray();
        return (alerts, cases, links, candidates);
    }

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
