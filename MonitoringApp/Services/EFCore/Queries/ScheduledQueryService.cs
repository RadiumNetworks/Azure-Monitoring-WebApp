using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static MonitoringApp.ScheduledQueryExecution;

namespace MonitoringApp;

public sealed class ScheduledQueryService(
    IDbContextFactory<AlertDbContext> factory,
    CrossTenantLogAnalyticsOptions options,
    TargetWorkspaceResolver resolver,
    AnalysisQueryRunner runner,
    AlertStore alerts,
    TimeProvider clock,
    ILogger<ScheduledQueryService> logger)
{
    public const int MaxTargets = ScheduledQueryExecution.MaxTargets;
    private readonly ScheduledQueryExecution execution = new(options, runner, clock, logger);

    public async Task<(int Pending, string? Error)> DeliveryStatusAsync(CancellationToken token = default)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        var pending = context.ScheduledQueryNotifications.Where(item => item.DeliveredUtc == null);
        return (await pending.CountAsync(token),
            await pending.Where(item => item.LastError != "").OrderBy(item => item.Id)
                .Select(item => item.LastError).FirstOrDefaultAsync(token));
    }

    public async Task<IReadOnlyList<ScheduledQueryRule>> ListAsync(CancellationToken token = default)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        return await context.ScheduledQueryRules.AsNoTracking().OrderBy(rule => rule.Name).ToArrayAsync(token);
    }

    public async Task<IReadOnlyList<ScheduledQueryTargetState>> StatesAsync(Guid id, CancellationToken token = default)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        return await context.ScheduledQueryTargetStates.AsNoTracking().Where(state => state.RuleId == id)
            .OrderBy(state => state.Computer).ToArrayAsync(token);
    }

    public Task SaveAsync(ScheduledQueryRule rule, CancellationToken token = default) =>
        SaveCoreAsync(rule, null, token);

    internal async Task SaveTemplateDraftAsync(ScheduledQueryTemplateDraft draft, CancellationToken token = default)
    {
        var provenance = draft.CaptureProvenance();
        await SaveCoreAsync(draft.Rule, provenance, token);
        draft.Complete();
    }

    private async Task SaveCoreAsync(ScheduledQueryRule rule, string? newTemplateProvenance, CancellationToken token)
    {
        rule = CopyDefinition(rule);
        Validate(rule);
        await using var context = await factory.CreateDbContextAsync(token);
        var stored = await context.ScheduledQueryRules.SingleOrDefaultAsync(item => item.Id == rule.Id, token);
        if (stored is null && rule.Revision != 0 || stored is not null && stored.Revision != rule.Revision)
            throw new DbUpdateConcurrencyException("This rule changed in another session. Refresh before editing.");
        if (stored is not null && stored.Scope != rule.Scope &&
            (stored.Enabled || rule.Enabled ||
                await context.ScheduledQueryTargetStates.AnyAsync(state => state.RuleId == rule.Id && state.ActiveAlertId != null, token)))
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.ScopeChangeNotAllowed, "Disable the rule and resolve existing alerts before changing its scope, or create a separate rule.");
        if (await context.ScheduledQueryRules.AnyAsync(item => item.Id != rule.Id && item.Name == rule.Name.Trim(), token))
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.NameExists, "A scheduled query rule with this name already exists.");
        if (stored is null)
        {
            stored = new() { Id = rule.Id };
            context.ScheduledQueryRules.Add(stored);
            stored.StandardTextProvenanceJson = newTemplateProvenance;
            stored.Name = rule.Name;
        }
        else if (newTemplateProvenance is not null)
            throw new InvalidOperationException("Template creation cannot mark an existing scheduled query rule.");
        stored.StandardTextProvenanceJson = StandardQueryTextProvenance.Retain(
            stored.StandardTextProvenanceJson, true, stored.Name, null, rule.Name, null);
        stored.Name = rule.Name.Trim();
        stored.Enabled = rule.Enabled;
        stored.Scope = rule.Scope;
        stored.WorkspaceKeys = rule.WorkspaceKeys.Select(key => key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        stored.WorkspaceKey = stored.WorkspaceKeys[0];
        stored.Domain = rule.Domain.Trim();
        stored.Site = rule.Site.Trim();
        stored.Role = rule.Role.Trim();
        stored.Computers = rule.Computers.Trim();
        stored.Kql = rule.Kql.Trim();
        stored.IntervalMinutes = rule.IntervalMinutes;
        stored.WindowMinutes = rule.WindowMinutes;
        stored.IngestionDelayMinutes = rule.IngestionDelayMinutes;
        stored.Comparison = rule.Comparison;
        stored.Threshold = rule.Threshold;
        stored.Severity = rule.Severity;
        stored.CategoryName = rule.CategoryName.Trim();
        stored.Revision++;
        stored.NextRunUtc = clock.GetUtcNow().UtcDateTime;
        stored.LastStatus = "Not evaluated";
        stored.LastError = "";
        stored.LastWorkspaceResultsJson = "[]";
        await context.SaveChangesAsync(token);
    }

    public async Task DeleteAsync(Guid id, int revision, CancellationToken token = default)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        if (await context.ScheduledQueryRules.Where(rule => rule.Id == id && rule.Revision == revision && !rule.Enabled)
            .ExecuteDeleteAsync(token) != 1)
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.DeleteNotAllowed, "Disable the rule first, and refresh if it changed in another session.");
    }

    public async Task DisableAsync(Guid id, int revision, CancellationToken token = default)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        if (await context.ScheduledQueryRules.Where(rule => rule.Id == id && rule.Revision == revision)
            .ExecuteUpdateAsync(update => update.SetProperty(rule => rule.Enabled, false)
                .SetProperty(rule => rule.Revision, rule => rule.Revision + 1), token) != 1)
            throw new DbUpdateConcurrencyException("This rule changed in another session. Refresh before disabling.");
    }

    public void Validate(ScheduledQueryRule rule) =>
        ScheduledQueryExecution.Validate(rule, options, requireConfiguredWorkspaces: true);

    private async Task<ScheduledQueryTarget[]> SelectTargetsAsync(
        ScheduledQueryRule rule, CrossTenantLogAnalyticsWorkspaceOptions workspace, CancellationToken token)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        return await SelectTargetsAsync(rule, workspace, context, token);
    }

    private async Task<ScheduledQueryTarget[]> SelectTargetsAsync(
        ScheduledQueryRule rule, CrossTenantLogAnalyticsWorkspaceOptions workspace, AlertDbContext context, CancellationToken token)
    {
        if (rule.Scope != AnalysisQueryScopes.SelectedSystems)
            return [new("", "", WorkspaceName: workspace.DisplayName, WorkspaceKey: workspace.Key)];
        var candidates = WorkspaceInventory(context, workspace).Where(item => !item.Maintenance);
        if (!string.IsNullOrWhiteSpace(rule.Domain))
            candidates = candidates.Where(item => item.Domain != null && item.Domain.Trim().ToUpper() == rule.Domain.Trim().ToUpper());
        if (!string.IsNullOrWhiteSpace(rule.Site))
            candidates = candidates.Where(item => item.Site != null && item.Site.Trim().ToUpper() == rule.Site.Trim().ToUpper());
        if (!string.IsNullOrWhiteSpace(rule.Role))
            candidates = candidates.Where(item => item.Role != null && item.Role.Trim().ToUpper() == rule.Role.Trim().ToUpper());
        var selected = rule.Computers.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToUpperInvariant()).Distinct().ToArray();
        if (selected.Length > 0)
            candidates = candidates.Where(item => selected.Contains(item.Computer.ToUpper()));
        var targets = await candidates.OrderBy(item => item.Computer).Take(MaxTargets + 1).ToArrayAsync(token);
        if (targets.Length > MaxTargets)
            throw new InvalidOperationException($"More than {MaxTargets} systems match. Narrow the rule filters.");
        foreach (var target in targets)
            if (!resolver.Resolve(target.SubscriptionId, target.Domain).Workspace.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The configured routes for {target.Computer} do not match this workspace.");
        if (targets.DistinctBy(item => item.Computer, StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            throw new InvalidOperationException("Computer names are ambiguous across subscriptions. Narrow the domain/system filters.");
        return targets.Select(target => new ScheduledQueryTarget(
            target.SubscriptionId, target.Computer, target.Site, target.ResourceGroup)).ToArray();
    }

    private static IQueryable<ComputerInventoryEntry> WorkspaceInventory(
        AlertDbContext context, CrossTenantLogAnalyticsWorkspaceOptions workspace)
    {
        var subscriptions = workspace.SubscriptionIds.Select(CrossTenantLogAnalyticsOptions.NormalizeSubscriptionId)
            .Where(value => value is not null).Select(value => value!.ToUpperInvariant()).ToArray();
        var domains = workspace.Domains.Select(CrossTenantLogAnalyticsOptions.NormalizeDomain)
            .SelectMany(value => new[] { value.ToUpperInvariant(), value.ToUpperInvariant() + "." }).ToArray();
        return context.ComputerInventory.AsNoTracking().Where(item =>
            (subscriptions.Contains(item.SubscriptionId.ToUpper()) || item.Domain != null && domains.Contains(item.Domain.Trim().ToUpper())));
    }

    public Task<ScheduledQueryEvaluation> PreviewAsync(ScheduledQueryRule rule, CancellationToken token = default,
        Action<ScheduledQueryExecutionPreview>? onPrepared = null) =>
        execution.EvaluateAsync(rule, SelectTargetsAsync, token, onPrepared, loadInventoryComputers: LoadInventoryComputersAsync);

    private async Task<IReadOnlyList<string>> LoadInventoryComputersAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, CancellationToken token)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        return await LoadInventoryComputersAsync(workspace, context, token);
    }

    private async Task<IReadOnlyList<string>> LoadInventoryComputersAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, AlertDbContext context, CancellationToken token)
    {
        var inventory = await WorkspaceInventory(context, workspace).ToArrayAsync(token);
        var computers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inventory)
        {
            // Use the effective route, not a rule's selected targets or its maintenance filters.
            if (!resolver.Resolve(entry.SubscriptionId, entry.Domain).Workspace.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase))
                continue;
            var computer = entry.Computer.Trim().TrimEnd('.');
            computers.Add(computer);
            if (!computer.Contains('.') && !string.IsNullOrWhiteSpace(entry.Domain))
                computers.Add($"{computer}.{entry.Domain.Trim().TrimEnd('.')}");
            if (computers.Count > MaxTargets * 2)
                throw new InvalidOperationException($"The workspace Inventory baseline exceeds {MaxTargets * 2} names.");
        }
        if (computers.Count == 0)
            throw new InvalidOperationException("The workspace has no Inventory baseline for _InventoryComputers.");
        return computers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        await DeliverAsync(token);
        Guid[] due;
        await using (var context = await factory.CreateDbContextAsync(token))
        {
            var now = clock.GetUtcNow().UtcDateTime;
            due = await context.ScheduledQueryRules.AsNoTracking()
                .Where(rule => rule.Enabled && rule.NextRunUtc <= now && (rule.LeaseUntilUtc == null || rule.LeaseUntilUtc <= now))
                .OrderBy(rule => rule.NextRunUtc).ThenBy(rule => rule.Id).Select(rule => rule.Id).Take(20).ToArrayAsync(token);
        }
        foreach (var id in due)
        {
            try { await EvaluateAsync(id, token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError("Scheduled KQL rule {RuleId} could not complete: {Error}", id, Bounded(exception.Message));
            }
            await DeliverAsync(token);
        }
    }

    private async Task EvaluateAsync(Guid id, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lease = Guid.NewGuid();
        int claimed;
        await using (var context = await factory.CreateDbContextAsync(token))
        {
            claimed = await context.ScheduledQueryRules
                .Where(rule => rule.Id == id && rule.Enabled && rule.NextRunUtc <= now &&
                    (rule.LeaseUntilUtc == null || rule.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(update => update.SetProperty(rule => rule.LeaseId, lease)
                    .SetProperty(rule => rule.LeaseUntilUtc, now.AddMinutes(5)), token);
        }
        if (claimed == 0)
            return;
        try
        {
            ScheduledQueryRule rule;
            await using (var context = await factory.CreateDbContextAsync(token))
                rule = await context.ScheduledQueryRules.AsNoTracking().SingleAsync(item => item.Id == id, token);
            if (!rule.Enabled)
                return;
            ScheduledQueryEvaluation? evaluation = null;
            string? error = null;
            try
            {
                evaluation = await execution.EvaluateAsync(rule, SelectTargetsAsync, token,
                    renewLease: () => RenewLeaseAsync(rule, lease, token),
                    loadInventoryComputers: LoadInventoryComputersAsync);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                error = $"The scheduled query timed out after {QueryTimeoutSeconds} seconds.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error = Bounded(exception.Message);
            }
            if (error is not null)
                logger.LogWarning("Scheduled KQL rule {RuleId} failed: {Error}", id, error);
            await CommitAsync(rule, lease, evaluation, error, token);
        }
        finally
        {
            // A lease also expires after process failure; never release another instance's lease.
            await using var context = await factory.CreateDbContextAsync(token);
            await context.ScheduledQueryRules.Where(rule => rule.Id == id && rule.LeaseId == lease)
                .ExecuteUpdateAsync(update => update.SetProperty(rule => rule.LeaseId, (Guid?)null)
                    .SetProperty(rule => rule.LeaseUntilUtc, (DateTime?)null), token);
        }
    }

    private async Task RenewLeaseAsync(ScheduledQueryRule rule, Guid lease, CancellationToken token)
    {
        var checkTime = clock.GetUtcNow().UtcDateTime;
        await using var context = await factory.CreateDbContextAsync(token);
        if (await context.ScheduledQueryRules.Where(item => item.Id == rule.Id && item.Enabled &&
            item.Revision == rule.Revision && item.LeaseId == lease && item.LeaseUntilUtc > checkTime)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseUntilUtc, checkTime.AddMinutes(5)), token) != 1)
            throw new DbUpdateConcurrencyException("The scheduled rule changed or its execution lease expired.");
    }

    private async Task CommitAsync(ScheduledQueryRule source, Guid lease, ScheduledQueryEvaluation? evaluation,
        string? error, CancellationToken token)
    {
        await DatabaseTransactions.ExecuteAsync(factory, async context =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var rule = await context.ScheduledQueryRules.SingleOrDefaultAsync(item => item.Id == source.Id, token);
            if (rule is null || !rule.Enabled || rule.Revision != source.Revision || rule.LeaseId != lease ||
                rule.LeaseUntilUtc is not DateTime until || until <= now)
                return;
            var states = await context.ScheduledQueryTargetStates.Where(state => state.RuleId == rule.Id).ToListAsync(token);
            var byTarget = states.ToDictionary(state => Key(state.SubscriptionId, state.Computer));
            foreach (var state in states)
            {
                state.Status = error is null ? "No data" : "Error";
                state.Value = null;
                state.CheckedUtc = now;
            }
            var workspaceStatuses = new List<ScheduledQueryWorkspaceStatus>();
            foreach (var workspaceResult in evaluation?.Workspaces ?? [])
            {
                var workspaceError = workspaceResult.Error;
                var currentTargets = new Dictionary<string, ScheduledQueryTarget>();
                var unknownTargets = rule.Scope == ScheduledQueryScopes.UnknownInventoryTargets;
                var knownComputers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (workspaceError is null)
                {
                    try
                    {
                        var workspace = options.Workspaces.Single(item =>
                            item.Key.Equals(workspaceResult.WorkspaceKey, StringComparison.OrdinalIgnoreCase));
                        if (unknownTargets)
                        {
                            knownComputers = (await LoadInventoryComputersAsync(workspace, context, token)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var stateNamespace = ScheduledQueryTarget.UnknownTargetNamespace(workspace.Key);
                            var previousTargets = states.Where(state => state.WorkspaceKey.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase) &&
                                state.SubscriptionId == stateNamespace && state.ActiveAlertId is not null)
                                .Select(state => new ScheduledQueryTarget("", state.Computer, WorkspaceKey: workspace.Key)
                                    { IsUnknownInventoryTarget = true });
                            currentTargets = workspaceResult.Targets.Concat(previousTargets)
                                .DistinctBy(target => Key(target.StateSubscriptionId, target.StateComputer))
                                .ToDictionary(target => Key(target.StateSubscriptionId, target.StateComputer));
                        }
                        else
                            currentTargets = (await SelectTargetsAsync(rule, workspace, context, token))
                                .ToDictionary(target => Key(target.StateSubscriptionId, target.StateComputer));
                    }
                    catch (InvalidOperationException exception)
                    {
                        workspaceError = Bounded(exception.Message);
                        logger.LogWarning("Scheduled KQL rule {RuleId}, workspace {WorkspaceKey} changed before commit: {Error}",
                            rule.Id, workspaceResult.WorkspaceKey, workspaceError);
                    }
                }
                if (workspaceError is not null)
                    foreach (var state in states.Where(state => state.WorkspaceKey.Equals(workspaceResult.WorkspaceKey, StringComparison.OrdinalIgnoreCase)))
                        state.Status = "Error";
                foreach (var target in unknownTargets && workspaceError is null ? currentTargets.Values.AsEnumerable() : workspaceResult.Targets)
                {
                    var key = Key(target.StateSubscriptionId, target.StateComputer);
                    if (workspaceError is null && !currentTargets.ContainsKey(key))
                        continue;
                    if (!byTarget.TryGetValue(key, out var state))
                    {
                        if (unknownTargets && knownComputers.Contains(target.Computer)) continue;
                        state = new() { RuleId = rule.Id, WorkspaceKey = workspaceResult.WorkspaceKey,
                            SubscriptionId = target.StateSubscriptionId, Computer = target.StateComputer };
                        context.ScheduledQueryTargetStates.Add(state);
                        states.Add(state);
                        byTarget.Add(key, state);
                    }
                    state.WorkspaceKey = workspaceResult.WorkspaceKey;
                    state.CheckedUtc = now;
                    if (workspaceError is not null)
                    {
                        state.Status = "Error";
                        state.Value = null;
                        continue;
                    }
                    var targetAbsent = unknownTargets && (knownComputers.Contains(target.Computer) || !workspaceResult.Values.ContainsKey(target.Series));
                    state.Value = unknownTargets
                        ? targetAbsent ? 0 : workspaceResult.Values.GetValueOrDefault(target.Series)
                        : workspaceResult.Values.GetValueOrDefault(target.Series);
                    state.Status = state.Value is null ? "No data" : !targetAbsent && rule.Matches(state.Value.Value) ? "Alerting" : "Normal";
                    if (state.Value is null)
                        continue;
                    var fired = state.Status == "Alerting";
                    if (fired && state.ActiveAlertId is null)
                    {
                        state.ActiveAlertId = $"scheduled-query:{Guid.NewGuid():N}";
                        state.FiredUtc = now;
                    }
                    else if (fired || state.ActiveAlertId is null)
                        continue;
                    context.ScheduledQueryNotifications.Add(new()
                    {
                        CreatedUtc = now,
                        PayloadJson = ScheduledQueryAlert.Payload(rule, currentTargets[key], state, now, fired,
                            workspaceResult.EventDetails?.GetValueOrDefault(target.Series))
                    });
                    if (!fired)
                    {
                        state.ActiveAlertId = null;
                        state.FiredUtc = null;
                    }
                }
                workspaceStatuses.Add(new(workspaceResult.WorkspaceKey,
                    workspaceError is null ? currentTargets.Count : workspaceResult.Targets.Count,
                    workspaceError is null ? currentTargets.Values.Count(target =>
                        workspaceResult.Values.TryGetValue(target.Series, out var value) && value.HasValue) : 0, workspaceError));
            }
            if (evaluation is not null)
                rule.LastWindowEndUtc = evaluation.WindowEndUtc;
            rule.LastWorkspaceResultsJson = JsonSerializer.Serialize(workspaceStatuses);
            var missing = states.Count(state => state.Status == "No data");
            var active = states.Count(state => state.ActiveAlertId is not null);
            var failed = workspaceStatuses.Count(workspace => workspace.Error is not null);
            rule.LastStatus = error is not null ? "Error" :
                failed == workspaceStatuses.Count && failed > 0 ? "Error" :
                failed > 0 ? $"Partial error: {failed}/{workspaceStatuses.Count} workspaces; {active} active; {missing} no data" :
                states.Count == 0 && rule.Scope != ScheduledQueryScopes.UnknownInventoryTargets
                    ? "No data: no matching targets" : $"{active} active; {missing} no data";
            rule.LastError = error ?? Bounded(string.Join("; ", workspaceStatuses.Where(workspace => workspace.Error is not null)
                .Select(workspace => $"{workspace.WorkspaceKey}: {workspace.Error}")));
            rule.LastRunUtc = now;
            var intervalTicks = TimeSpan.FromMinutes(rule.IntervalMinutes).Ticks;
            var elapsed = Math.Max(0, now.Ticks - source.NextRunUtc.Ticks);
            rule.NextRunUtc = source.NextRunUtc.AddTicks((elapsed / intervalTicks + 1) * intervalTicks);
            rule.LeaseId = null;
            rule.LeaseUntilUtc = null;
            await context.SaveChangesAsync(token);
        }, token, IsolationLevel.Serializable);
    }

    public async Task DeliverAsync(CancellationToken token)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        var pending = await context.ScheduledQueryNotifications.Where(item => item.DeliveredUtc == null)
            .OrderBy(item => item.Id).Take(100).ToArrayAsync(token);
        foreach (var notification in pending)
        {
            try
            {
                using var payload = JsonDocument.Parse(notification.PayloadJson);
                await alerts.AddAsync(payload.RootElement, token);
                notification.DeliveredUtc = clock.GetUtcNow().UtcDateTime;
                notification.LastError = "";
                await context.SaveChangesAsync(token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                notification.LastError = Bounded(exception.Message);
                logger.LogError("Scheduled query alert delivery {NotificationId} failed: {Error}", notification.Id, notification.LastError);
                await context.SaveChangesAsync(token);
                break;
            }
        }
    }

    private static string Key(string subscription, string computer) =>
        $"{subscription.ToUpperInvariant()}\0{computer.ToUpperInvariant()}";
}
