using System.Data;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class RcaService(
    IDbContextFactory<AlertDbContext> factory,
    IAuthorizationService authorization,
    CurrentUserIdentity identity,
    ApplicationAuthenticationOptions authentication,
    TimeProvider timeProvider,
    AlertRuleEvaluator evaluator,
    QueryResultPresenter presenter)
{
    public bool DirectoryAssigneesEnabled => identity.DirectoryEnabled && !authentication.IsOpen;

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
            throw new RcaValidationException(RcaValidationCode.NameInvalid, "Enter an RCA name of 1-256 characters without control characters.");
        if (selections.Count == 0 || selections.Select(selection => selection.AlertEventId).Distinct().Count() != selections.Count)
            throw new RcaValidationException(RcaValidationCode.SelectionsInvalid, "Select at least one alert, without duplicate selections.");
        var actor = await identity.GetActorAsync(user, authentication.IsOpen, cancellationToken);
        var targetId = id ?? Guid.NewGuid();
        try
        {
            await DatabaseTransactions.ExecuteAsync(factory, async context =>
            {
                var now = timeProvider.GetUtcNow();
                var state = await LoadAssignmentsAsync(context, now, cancellationToken);
                var target = id is null ? new RootCauseAnalysis
                {
                    Id = targetId,
                    CategoryName = name!,
                    RuleId = Guid.Empty,
                    CreatedAt = now,
                    FollowUpDueAt = now.AddMinutes(15),
                    Revision = 1
                } : state.Cases.SingleOrDefault(rca => rca.Id == id)
                    ?? throw new RcaValidationException(RcaValidationCode.CaseMissing, "The selected RCA no longer exists.");
                if (id is not null && target.Revision != revision)
                    throw new RcaValidationException(RcaValidationCode.Changed, "This RCA changed. Refresh and reapply your changes.");
                if (target.Status == RcaStatuses.Closed)
                    throw new RcaValidationException(RcaValidationCode.Closed, "Reopen the RCA before assigning alerts.");

                var candidates = state.Candidates.ToDictionary(candidate => candidate.Alert.Id);
                var selected = selections.Select(selection =>
                {
                    if (!candidates.TryGetValue(selection.AlertEventId, out var candidate))
                        throw new RcaValidationException(RcaValidationCode.AlertUnavailable, "A selected alert is no longer active or within the last 24 hours. Refresh the alert list.");
                    if (candidate.RcaId != selection.ExpectedRcaId || candidate.RcaRevision != selection.ExpectedRcaRevision)
                        throw new RcaValidationException(RcaValidationCode.AssignmentChanged, "An alert's RCA assignment changed. Refresh the alert list and confirm the current assignment.");
                    if (candidate.RcaId is not null && candidate.RcaId != targetId && !confirmReassignment)
                        throw new RcaValidationException(RcaValidationCode.ReassignmentRequired, "Confirm moving alerts from their existing RCA before saving.");
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
                            candidate.Alert.Id,
                            PreviousRcaId = candidate.RcaId,
                            PreviousRcaName = candidate.RcaName
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
            }, cancellationToken, IsolationLevel.Serializable);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RcaValidationException(RcaValidationCode.Changed, "An RCA changed. Refresh and reapply your changes.", exception);
        }
        return targetId;
    }

    private async Task RequireAdministratorAsync(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may create RCAs or assign alerts.");
    }

    private static Guid[] ReadEvidence(RootCauseAnalysis rca) =>
        RcaEvidence.Read(rca.Id, rca.EvidenceEventIdsJson);

    private static async Task<(AlertRecord[] Alerts, RootCauseAnalysis[] Cases, RcaAlertLink[] Links, RcaAlertCandidate[] Candidates)> LoadAssignmentsAsync(
        AlertDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await context.Alerts.AsNoTracking().ToArrayAsync(cancellationToken);
        var alerts = stored.Where(alert => !InventoryHeartbeatBatch.IsInventoryHeartbeat(alert)).ToArray();
        var cases = await context.RootCauseAnalyses.ToArrayAsync(cancellationToken);
        var links = await context.RcaAlertLinks.ToArrayAsync(cancellationToken);
        var candidates = RcaAssignmentCandidates.Build(alerts, cases, links, now);
        return (alerts, cases, links, candidates);
    }

    public async Task<IReadOnlyList<OpenRcaNotice>> GetOpenNoticesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var cases = await context.RootCauseAnalyses.AsNoTracking()
            .Where(rca => rca.Status != "Closed")
            .Select(rca => new
            {
                rca.Id,
                rca.CategoryName,
                rca.CreatedAt,
                rca.EvidenceEventIdsJson,
                rca.Status,
                rca.AssignedTo
            })
            .ToArrayAsync(cancellationToken);
        if (cases.Length == 0)
            return [];

        var evidence = cases.ToDictionary(rca => rca.Id, rca =>
            RcaEvidence.Read(rca.Id, rca.EvidenceEventIdsJson));
        var eventIds = evidence.Values.SelectMany(ids => ids).Distinct().ToArray();
        var domains = await context.ParsedAlerts.AsNoTracking()
            .Where(alert => eventIds.Contains(alert.Id) && alert.InventoryComputerEntry != null)
            .Select(alert => new { alert.Id, alert.InventoryComputerEntry!.Domain })
            .ToDictionaryAsync(alert => alert.Id, alert => alert.Domain, cancellationToken);

        var labels = await UserReferenceQueries.LabelsAsync(context, cases.Select(rca => rca.AssignedTo), identity.DirectoryEnabled, cancellationToken, DirectoryAssigneesEnabled);
        return cases.OrderByDescending(rca => rca.CreatedAt).ThenBy(rca => rca.Id)
            .Select(rca => new OpenRcaNotice(rca.Id, rca.CategoryName,
                evidence[rca.Id].Select(id => domains.GetValueOrDefault(id))
                    .OfType<string>().Select(domain => domain.Trim()).Where(domain => domain.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                rca.Status, rca.AssignedTo)
            { AssigneeDisplayName = labels.GetValueOrDefault(rca.AssignedTo) })
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
        var labels = await UserReferenceQueries.LabelsAsync(context, cases.Select(rca => rca.AssignedTo), identity.DirectoryEnabled, cancellationToken, DirectoryAssigneesEnabled);
        foreach (var rca in cases) rca.AssigneeDisplayName = labels.GetValueOrDefault(rca.AssignedTo);
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
        var labels = await UserReferenceQueries.LabelsAsync(context, changes.SelectMany(change =>
            change.Field == "AssignedTo" ? new[] { change.Actor, change.Before, change.After } : [change.Actor]),
            identity.DirectoryEnabled, default, DirectoryAssigneesEnabled);
        foreach (var change in changes)
        {
            change.ActorDisplayName = labels.GetValueOrDefault(change.Actor);
            if (change.Field != "AssignedTo") continue;
            change.BeforeDisplayName = labels.GetValueOrDefault(change.Before);
            change.AfterDisplayName = labels.GetValueOrDefault(change.After);
        }
        return changes.OrderByDescending(change => change.CreatedAt).ThenBy(change => change.Field).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetAssigneesAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may list RCA assignees.");

        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.AuthenticationUsers.AsNoTracking()
            .Where(account => !DirectoryAssigneesEnabled || SqlAuthenticationRoles.All.Contains(account.Role))
            .Select(account => account.Username).ToArrayAsync(cancellationToken);
        var knownIdentities = await HistoricalUserQueries.AliasesAsync(context, cancellationToken);
        var currentIdentity = identity.GetDisplayName(user);
        var legacy = accounts.Select(account => account.Trim())
            .Concat(knownIdentities
                .Append(currentIdentity ?? string.Empty)
                .Select(candidate => candidate.Trim()).Where(IsEmailAddress))
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identity.DirectoryEnabled)
        {
            var historicalProfiles = HistoricalUserQueries.Profiles(context);
            var groups = await (
                from link in context.HistoricalUserAliases.AsNoTracking()
                join alias in historicalProfiles on link.AliasUserId equals alias.Id
                join main in historicalProfiles on link.MainUserId equals main.Id
                select new { Alias = alias.UserName, Main = main.UserName }
            ).ToArrayAsync(cancellationToken);
            if (groups.Length > 0)
            {
                var reserved = await HistoricalUserQueries.ReservedAliasesAsync(context, cancellationToken);
                var groupedAliases = groups.Where(group => !reserved.Contains(group.Alias) && !reserved.Contains(group.Main))
                    .Select(group => group.Alias.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                legacy = legacy.Where(value => !groupedAliases.Contains(value)).ToArray();
            }
        }
        if (!DirectoryAssigneesEnabled) return legacy;
        var unavailable = await UserReferenceQueries.UnavailableNamesAsync(context, cancellationToken);
        var available = await UserReferenceQueries.AvailableUsers(context).Select(entry => entry.Id).ToArrayAsync(cancellationToken);
        var historical = HistoricalUserQueries.Profiles(context);
        var profiles = await context.DirectoryUsers.AsNoTracking().Where(entry => !historical.Any(profile => profile.Id == entry.Id))
            .Select(entry => new { entry.UserName, entry.Email }).ToArrayAsync(cancellationToken);
        var directoryNames = profiles.SelectMany(entry => new[] { entry.UserName, entry.Email ?? "" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        directoryNames.UnionWith(await context.LegacyUserLinks.AsNoTracking()
            .Where(link => link.Status == LegacyUserLinkStatuses.Confirmed && link.TargetUserId.HasValue)
            .Select(link => link.Alias).ToArrayAsync(cancellationToken));
        return legacy.Where(value => !unavailable.Contains(value) && !directoryNames.Contains(value)).Concat(available.Select(DirectoryIdentity.OwnerKey))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<UserReference>> GetAssigneeOptionsAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var keys = await GetAssigneesAsync(user, cancellationToken);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var labels = await UserReferenceQueries.LabelsAsync(context, keys, identity.DirectoryEnabled, cancellationToken, DirectoryAssigneesEnabled);
        var references = keys.Select(key => new UserReference(key, labels.GetValueOrDefault(key, key))).ToArray();
        var ambiguous = references.GroupBy(reference => reference.Label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return references.Select(reference => ambiguous.Contains(reference.Label)
                ? reference with { Label = $"{reference.Label} [{reference.Key}]" } : reference)
            .OrderBy(reference => reference.Label, StringComparer.OrdinalIgnoreCase).ThenBy(reference => reference.Key).ToArray();
    }

    public async Task SaveAsync(
        ClaimsPrincipal user, Guid id, int revision, string analysis, string assignedTo,
        string status, string comment, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded)
            throw new UnauthorizedAccessException("Only administrators may edit RCAs.");
        if (!RcaStatuses.All.Contains(status))
            throw new RcaValidationException(RcaValidationCode.StatusInvalid, "Unknown RCA status.");
        assignedTo = assignedTo.Trim();
        if (DirectoryAssigneesEnabled && DirectoryIdentity.TryReadUserId(assignedTo, out var selectedUserId))
            assignedTo = DirectoryIdentity.OwnerKey(selectedUserId);
        if (analysis.Length > 100_000 || comment.Length > 4000 || assignedTo.Length > 256)
            throw new RcaValidationException(RcaValidationCode.LengthExceeded, "Analysis, comment or assignee exceeds the allowed length.");

        var actor = await identity.GetActorAsync(user, authentication.IsOpen, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await DatabaseTransactions.ExecuteAsync(factory, async context =>
        {
            var rca = await context.RootCauseAnalyses.SingleOrDefaultAsync(rca => rca.Id == id, cancellationToken)
                ?? throw new RcaValidationException(RcaValidationCode.CaseMissing, "The selected RCA no longer exists.");
            if (rca.Revision != revision)
                throw new RcaValidationException(RcaValidationCode.Changed, "This RCA changed. Refresh and reapply your changes.");
            if (assignedTo.Length > 0 && assignedTo != rca.AssignedTo)
            {
                var valid = IsEmailAddress(assignedTo) || await context.AuthenticationUsers.AnyAsync(
                    candidate => candidate.Username == assignedTo &&
                        (!DirectoryAssigneesEnabled || SqlAuthenticationRoles.All.Contains(candidate.Role)), cancellationToken);
                if (DirectoryAssigneesEnabled)
                {
                    if (DirectoryIdentity.TryReadUserId(assignedTo, out var target))
                    {
                        valid = await UserReferenceQueries.AvailableUsers(context).AnyAsync(entry => entry.Id == target, cancellationToken);
                        assignedTo = DirectoryIdentity.OwnerKey(target);
                    }
                    else if ((await UserReferenceQueries.UnavailableNamesAsync(context, cancellationToken)).Contains(assignedTo))
                        valid = false;
                }
                if (!valid)
                    throw new RcaValidationException(RcaValidationCode.AssigneeInvalid, "Enter an email address or select an active existing user as assignee.");
            }

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
                throw new RcaValidationException(RcaValidationCode.Changed, "This RCA changed. Refresh and reapply your changes.", exception);
            }
        }, cancellationToken, DirectoryAssigneesEnabled ? IsolationLevel.Serializable : null);
    }

    private static bool IsEmailAddress(string value) =>
        value.Length <= 256 && value.Contains('@') && !value.Any(char.IsControl) &&
        MailAddress.TryCreate(value, out var address) &&
        value.Equals(address.Address, StringComparison.OrdinalIgnoreCase);
}
