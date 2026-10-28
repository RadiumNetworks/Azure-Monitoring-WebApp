using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

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
    public const int MaxTargets = 1000;
    private const int QueryTimeoutSeconds = 90;

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

    public async Task SaveAsync(ScheduledQueryRule rule, CancellationToken token = default)
    {
        rule = CopyDefinition(rule);
        Validate(rule);
        await using var context = await factory.CreateDbContextAsync(token);
        var stored = await context.ScheduledQueryRules.SingleOrDefaultAsync(item => item.Id == rule.Id, token);
        if (stored is null && rule.Revision != 0 || stored is not null && stored.Revision != rule.Revision)
            throw new DbUpdateConcurrencyException("This rule changed in another session. Refresh before editing.");
        if (await context.ScheduledQueryRules.AnyAsync(item => item.Id != rule.Id && item.Name == rule.Name.Trim(), token))
            throw new InvalidOperationException("A scheduled query rule with this name already exists.");
        if (stored is null)
        {
            stored = new() { Id = rule.Id };
            context.ScheduledQueryRules.Add(stored);
        }
        stored.Name = rule.Name.Trim();
        stored.Enabled = rule.Enabled;
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
            throw new InvalidOperationException("Disable the rule first, and refresh if it changed in another session.");
    }

    public async Task DisableAsync(Guid id, int revision, CancellationToken token = default)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        if (await context.ScheduledQueryRules.Where(rule => rule.Id == id && rule.Revision == revision)
            .ExecuteUpdateAsync(update => update.SetProperty(rule => rule.Enabled, false)
                .SetProperty(rule => rule.Revision, rule => rule.Revision + 1), token) != 1)
            throw new DbUpdateConcurrencyException("This rule changed in another session. Refresh before disabling.");
    }

    public void Validate(ScheduledQueryRule rule) => Validate(rule, requireConfiguredWorkspaces: true);

    private void Validate(ScheduledQueryRule rule, bool requireConfiguredWorkspaces)
    {
        if (!options.Enabled)
            throw new InvalidOperationException("Cross-tenant Log Analytics must be enabled in Admin Settings.");
        if (rule.Id == Guid.Empty || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 256)
            throw new InvalidOperationException("A rule ID and a name of at most 256 characters are required.");
        if (rule.WorkspaceKeys.Count == 0 || rule.WorkspaceKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Trim().Length > 128))
            throw new InvalidOperationException("Select at least one workspace with a key of at most 128 characters.");
        if (requireConfiguredWorkspaces && rule.WorkspaceKeys.Any(key =>
            !options.Workspaces.Any(workspace => workspace.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Every selected workspace must be configured. Remove unavailable workspaces before saving.");
        if (new[] { rule.Domain, rule.Site, rule.Role, rule.CategoryName }.Any(value => value.Trim().Length > 256) ||
            string.IsNullOrWhiteSpace(rule.CategoryName) || rule.Computers.Length > 4000)
            throw new InvalidOperationException("Filters/category may contain at most 256 characters; the computer list at most 4000.");
        if (rule.IntervalMinutes is < 1 or > 1440 || rule.WindowMinutes is < 1 or > 1440 ||
            rule.IngestionDelayMinutes is < 0 or > 60)
            throw new InvalidOperationException("Interval/window must be 1-1440 minutes and ingestion delay 0-60 minutes.");
        if (!double.IsFinite(rule.Threshold) || !ScheduledQueryRule.Comparisons.Contains(rule.Comparison) ||
            rule.Severity is not ("Sev0" or "Sev1" or "Sev2" or "Sev3" or "Sev4"))
            throw new InvalidOperationException("Select a supported comparison, finite threshold and severity.");
        var errors = AnalysisQueryValidator.ValidateKql(rule.Kql, AnalysisQueryVisualizationTypes.Stat).ToList();
        if (!rule.Kql.Contains("_WindowStart", StringComparison.Ordinal) || !rule.Kql.Contains("_WindowEnd", StringComparison.Ordinal))
            errors.Add("KQL must use _WindowStart and _WindowEnd to restrict the evaluation window.");
        if (rule.Kql.Length > 65536)
            errors.Add("KQL may contain at most 65536 characters.");
        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }

    private async Task<ComputerInventoryEntry[]> SelectTargetsAsync(
        ScheduledQueryRule rule, CrossTenantLogAnalyticsWorkspaceOptions workspace, AlertDbContext context, CancellationToken token)
    {
        var subscriptions = workspace.SubscriptionIds.Select(CrossTenantLogAnalyticsOptions.NormalizeSubscriptionId)
            .Where(value => value is not null).Select(value => value!.ToUpperInvariant()).ToArray();
        var domains = workspace.Domains.Select(CrossTenantLogAnalyticsOptions.NormalizeDomain)
            .SelectMany(value => new[] { value.ToUpperInvariant(), value.ToUpperInvariant() + "." }).ToArray();
        var candidates = context.ComputerInventory.AsNoTracking().Where(item => !item.Maintenance &&
            (subscriptions.Contains(item.SubscriptionId.ToUpper()) || item.Domain != null && domains.Contains(item.Domain.Trim().ToUpper())));
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
        return targets;
    }

    public Task<ScheduledQueryEvaluation> PreviewAsync(ScheduledQueryRule rule, CancellationToken token = default,
        Action<ScheduledQueryExecutionPreview>? onPrepared = null) =>
        EvaluateWorkspacesAsync(rule, token, onPrepared);

    private async Task<ScheduledQueryEvaluation> EvaluateWorkspacesAsync(ScheduledQueryRule rule, CancellationToken token,
        Action<ScheduledQueryExecutionPreview>? onPrepared = null, Func<Task>? renewLease = null)
    {
        rule = CopyDefinition(rule);
        Validate(rule, requireConfiguredWorkspaces: false);
        var runStart = clock.GetUtcNow().UtcDateTime;
        var delayed = runStart.AddMinutes(-rule.IngestionDelayMinutes);
        var end = new DateTime(delayed.Ticks - delayed.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var start = end.AddMinutes(-rule.WindowMinutes);
        var evaluations = new List<ScheduledQueryWorkspaceEvaluation>();
        foreach (var key in rule.WorkspaceKeys.Select(key => key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (renewLease is not null)
                await renewLease();
            ComputerInventoryEntry[] targets = [];
            var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            string? error = null;
            try
            {
                var workspace = options.Workspaces.SingleOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("This workspace is no longer configured.");
                await using var context = await factory.CreateDbContextAsync(token);
                targets = await SelectTargetsAsync(rule, workspace, context, token);
                var kql = $$"""
                    set truncationmaxsize=1048576;
                    let _WindowStart = datetime({{start:O}});
                    let _WindowEnd = datetime({{end:O}});
                    let _ScheduledQueryResult = () {
                    {{rule.Kql}}
                    };
                    _ScheduledQueryResult()
                    | project Series, Value
                    | take {{MaxTargets + 1}}
                    """;
                // Later workspaces must still include the same absolute window after earlier requests.
                var elapsedMinutes = (int)Math.Ceiling(Math.Max(0, (clock.GetUtcNow().UtcDateTime - runStart).TotalMinutes));
                var lookback = Math.Max(5, rule.WindowMinutes + rule.IngestionDelayMinutes +
                    (QueryTimeoutSeconds + 59) / 60 + 1 + elapsedMinutes);
                var systems = targets.Select(item => item.Computer).ToArray();
                onPrepared?.Invoke(new(new(workspace.Key, workspace.DisplayName, systems,
                    AnalysisQueryRunner.BuildEffectiveKql(kql, systems, 1, lookback,
                        visualizationType: AnalysisQueryVisualizationTypes.Stat)), workspace.WorkspaceId, start, end, lookback));
                if (targets.Length > 0)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(QueryTimeoutSeconds));
                    var result = await runner.TryQueryAsync(workspace, kql, systems,
                        lookback, 1, timeout.Token, AnalysisQueryVisualizationTypes.Stat);
                    if (result.Error is not null)
                        throw new InvalidOperationException(result.Error);
                    var table = result.Tables?.SingleOrDefault() ?? throw new InvalidOperationException("The query returned no result table.");
                    var seriesIndex = table.Columns.ToList().FindIndex(column => column.Name == "Series");
                    var valueIndex = table.Columns.ToList().FindIndex(column => column.Name == "Value");
                    var names = targets.Select(item => item.Computer).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in table.Rows)
                    {
                        var computer = row[seriesIndex]!;
                        if (!names.Contains(computer) || !values.TryAdd(computer,
                            row[valueIndex] is null ? null : double.Parse(row[valueIndex]!, CultureInfo.InvariantCulture)))
                            throw new InvalidOperationException("KQL must return at most one Series/Value row per selected Computer, without unknown or duplicate systems.");
                    }
                }
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
            {
                values.Clear();
                logger.LogWarning("Scheduled KQL rule {RuleId}, workspace {WorkspaceKey} failed: {Error}", rule.Id, key, error);
            }
            evaluations.Add(new(key, targets, values, error));
        }
        return new(end, evaluations);
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        await DeliverAsync(token);
        await using (var context = await factory.CreateDbContextAsync(token))
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var due = await context.ScheduledQueryRules.AsNoTracking()
                .Where(rule => rule.Enabled && rule.NextRunUtc <= now && (rule.LeaseUntilUtc == null || rule.LeaseUntilUtc <= now))
                .OrderBy(rule => rule.NextRunUtc).ThenBy(rule => rule.Id).Select(rule => rule.Id).Take(20).ToArrayAsync(token);
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
    }

    private async Task EvaluateAsync(Guid id, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lease = Guid.NewGuid();
        await using var context = await factory.CreateDbContextAsync(token);
        var claimed = await context.ScheduledQueryRules
            .Where(rule => rule.Id == id && rule.Enabled && rule.NextRunUtc <= now &&
                (rule.LeaseUntilUtc == null || rule.LeaseUntilUtc <= now))
            .ExecuteUpdateAsync(update => update.SetProperty(rule => rule.LeaseId, lease)
                .SetProperty(rule => rule.LeaseUntilUtc, now.AddMinutes(5)), token);
        if (claimed == 0)
            return;
        try
        {
            var rule = await context.ScheduledQueryRules.AsNoTracking().SingleAsync(item => item.Id == id, token);
            if (!rule.Enabled)
                return;
            ScheduledQueryEvaluation? evaluation = null;
            string? error = null;
            try
            {
                evaluation = await EvaluateWorkspacesAsync(rule, token, renewLease: async () =>
                {
                    var checkTime = clock.GetUtcNow().UtcDateTime;
                    if (await context.ScheduledQueryRules.Where(item => item.Id == id && item.Enabled &&
                        item.Revision == rule.Revision && item.LeaseId == lease && item.LeaseUntilUtc > checkTime)
                        .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseUntilUtc, checkTime.AddMinutes(5)), token) != 1)
                        throw new DbUpdateConcurrencyException("The scheduled rule changed or its execution lease expired.");
                });
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
            await context.ScheduledQueryRules.Where(rule => rule.Id == id && rule.LeaseId == lease)
                .ExecuteUpdateAsync(update => update.SetProperty(rule => rule.LeaseId, (Guid?)null)
                    .SetProperty(rule => rule.LeaseUntilUtc, (DateTime?)null), token);
        }
    }

    private async Task CommitAsync(ScheduledQueryRule source, Guid lease, ScheduledQueryEvaluation? evaluation,
        string? error, CancellationToken token)
    {
        await using var strategyContext = await factory.CreateDbContextAsync(token);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(token);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
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
                var currentTargets = new Dictionary<string, ComputerInventoryEntry>();
                if (workspaceError is null)
                {
                    try
                    {
                        var workspace = options.Workspaces.Single(item =>
                            item.Key.Equals(workspaceResult.WorkspaceKey, StringComparison.OrdinalIgnoreCase));
                        currentTargets = (await SelectTargetsAsync(rule, workspace, context, token))
                            .ToDictionary(target => Key(target.SubscriptionId, target.Computer));
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
                foreach (var target in workspaceResult.Targets)
                {
                    if (workspaceError is null && !currentTargets.ContainsKey(Key(target.SubscriptionId, target.Computer)))
                        continue;
                    if (!byTarget.TryGetValue(Key(target.SubscriptionId, target.Computer), out var state))
                    {
                        state = new() { RuleId = rule.Id, SubscriptionId = target.SubscriptionId, Computer = target.Computer };
                        context.ScheduledQueryTargetStates.Add(state);
                        states.Add(state);
                        byTarget.Add(Key(target.SubscriptionId, target.Computer), state);
                    }
                    state.WorkspaceKey = workspaceResult.WorkspaceKey;
                    state.CheckedUtc = now;
                    if (workspaceError is not null)
                    {
                        state.Status = "Error";
                        state.Value = null;
                        continue;
                    }
                    state.Value = workspaceResult.Values.GetValueOrDefault(target.Computer);
                    state.Status = state.Value is null ? "No data" : rule.Matches(state.Value.Value) ? "Alerting" : "Normal";
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
                        PayloadJson = ScheduledQueryAlert.Payload(rule, currentTargets[Key(target.SubscriptionId, target.Computer)], state, now, fired)
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
                        workspaceResult.Values.TryGetValue(target.Computer, out var value) && value.HasValue) : 0, workspaceError));
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
                states.Count == 0 ? "No data: no matching systems" : $"{active} active; {missing} no data";
            rule.LastError = error ?? Bounded(string.Join("; ", workspaceStatuses.Where(workspace => workspace.Error is not null)
                .Select(workspace => $"{workspace.WorkspaceKey}: {workspace.Error}")));
            rule.LastRunUtc = now;
            var intervalTicks = TimeSpan.FromMinutes(rule.IntervalMinutes).Ticks;
            var elapsed = Math.Max(0, now.Ticks - source.NextRunUtc.Ticks);
            rule.NextRunUtc = source.NextRunUtc.AddTicks((elapsed / intervalTicks + 1) * intervalTicks);
            rule.LeaseId = null;
            rule.LeaseUntilUtc = null;
            await context.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
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

    private static string Key(string subscription, string computer) => $"{subscription.ToUpperInvariant()}\0{computer.ToUpperInvariant()}";
    private static string Bounded(string message) => message.Length <= 2048 ? message : message[..2048];

    private static ScheduledQueryRule CopyDefinition(ScheduledQueryRule source) => new()
    {
        Id = source.Id, Revision = source.Revision, Name = source.Name, Enabled = source.Enabled,
        WorkspaceKey = source.WorkspaceKey, WorkspaceKeysJson = source.WorkspaceKeysJson,
        Domain = source.Domain, Site = source.Site, Role = source.Role,
        Computers = source.Computers, Kql = source.Kql, IntervalMinutes = source.IntervalMinutes,
        WindowMinutes = source.WindowMinutes, IngestionDelayMinutes = source.IngestionDelayMinutes,
        Comparison = source.Comparison, Threshold = source.Threshold, Severity = source.Severity,
        CategoryName = source.CategoryName
    };
}

public sealed class ScheduledQueryWorker(
    ScheduledQueryService service, DatabaseConfigurationStatus database,
    TimeProvider clock, ILogger<ScheduledQueryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!database.IsValid)
        {
            logger.LogError("Scheduled KQL alerts cannot run: {Error}", database.Error);
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try { await service.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled KQL alert checks failed. Check database connectivity and apply the scheduled-query migration.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
