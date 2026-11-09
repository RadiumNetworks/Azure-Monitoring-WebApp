namespace MonitoringApp;

internal sealed class ScheduledQueryExecution(
    CrossTenantLogAnalyticsOptions options, AnalysisQueryRunner runner, TimeProvider clock,
    ILogger<ScheduledQueryService> logger)
{
    internal const int MaxTargets = KqlQueryLimits.MaxScheduledTargets;
    internal const int QueryTimeoutSeconds = 90;

    internal static void Validate(ScheduledQueryRule rule, CrossTenantLogAnalyticsOptions options, bool requireConfiguredWorkspaces)
    {
        if (!options.Enabled)
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.LiveDisabled, "Cross-tenant Log Analytics must be enabled in Admin Settings.");
        ValidateIdentityAndScope(rule);
        if (rule.WorkspaceKeys.Count == 0 || rule.WorkspaceKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Trim().Length > 128))
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.WorkspaceSelectionInvalid, "Select at least one workspace with a key of at most 128 characters.");
        if (requireConfiguredWorkspaces && rule.WorkspaceKeys.Any(key =>
            !options.Workspaces.Any(workspace => workspace.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))))
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.WorkspaceUnavailable, "Every selected workspace must be configured. Remove unavailable workspaces before saving.");
        ValidateParametersAndKql(rule);
    }

    internal static void ValidateTemplate(ScheduledQueryRule rule)
    {
        ValidateIdentityAndScope(rule);
        ValidateParametersAndKql(rule);
    }

    private static void ValidateIdentityAndScope(ScheduledQueryRule rule)
    {
        if (rule.Id == Guid.Empty || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 256)
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.IdentityInvalid, "A rule ID and a name of at most 256 characters are required.");
        if (!AnalysisQueryScopes.All.Contains(rule.Scope))
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.ScopeInvalid, "Select a supported rule scope (SelectedSystems or Workspace).");
        if (rule.Scope == AnalysisQueryScopes.Workspace &&
            new[] { rule.Domain, rule.Site, rule.Role, rule.Computers }.Any(value => !string.IsNullOrWhiteSpace(value)))
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.WorkspaceFiltersInvalid, "Workspace-wide rules do not accept inventory filters. Clear Domain, Site, Role and Computers.");
    }

    private static void ValidateParametersAndKql(ScheduledQueryRule rule)
    {
        if (new[] { rule.Domain, rule.Site, rule.Role, rule.CategoryName }.Any(value => value.Trim().Length > 256) ||
            string.IsNullOrWhiteSpace(rule.CategoryName) || rule.Computers.Length > 4000)
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.FiltersInvalid, "Filters/category may contain at most 256 characters; the computer list at most 4000.");
        if (rule.IntervalMinutes is < 1 or > 1440 || rule.WindowMinutes is < 1 or > 1440 ||
            rule.IngestionDelayMinutes is < 0 or > 60)
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.TimingInvalid, "Interval/window must be 1-1440 minutes and ingestion delay 0-60 minutes.");
        if (!double.IsFinite(rule.Threshold) || !ScheduledQueryRule.Comparisons.Contains(rule.Comparison) ||
            rule.Severity is not ("Sev0" or "Sev1" or "Sev2" or "Sev3" or "Sev4"))
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.ConditionInvalid, "Select a supported comparison, finite threshold and severity.");
        var errors = AnalysisQueryValidator.ValidateScheduledKql(rule.Kql, rule.Scope);
        if (errors.Count != 0)
            throw new ScheduledQueryValidationException(ScheduledQueryValidationCode.KqlInvalid, string.Join(" ", errors));
    }

    internal async Task<ScheduledQueryEvaluation> EvaluateAsync(
        ScheduledQueryRule rule,
        Func<ScheduledQueryRule, CrossTenantLogAnalyticsWorkspaceOptions, CancellationToken, Task<ScheduledQueryTarget[]>> selectTargets,
        CancellationToken token,
        Action<ScheduledQueryExecutionPreview>? onPrepared = null, Func<Task>? renewLease = null)
    {
        rule = CopyDefinition(rule);
        Validate(rule, options, requireConfiguredWorkspaces: false);
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
            ScheduledQueryTarget[] targets = [];
            var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            var eventDetails = new Dictionary<string, ScheduledQueryEventDetails>(StringComparer.OrdinalIgnoreCase);
            string? error = null;
            Exception? failure = null;
            try
            {
                var workspace = options.Workspaces.SingleOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("This workspace is no longer configured.");
                targets = await selectTargets(rule, workspace, token);
                // Later workspaces must still include the same absolute window after earlier requests.
                var elapsedMinutes = (int)Math.Ceiling(Math.Max(0, (clock.GetUtcNow().UtcDateTime - runStart).TotalMinutes));
                var lookback = Math.Max(5, rule.WindowMinutes + rule.IngestionDelayMinutes +
                    (QueryTimeoutSeconds + 59) / 60 + 1 + elapsedMinutes);
                var systems = targets.Where(item => item.WorkspaceName is null).Select(item => item.Computer).ToArray();
                var prepared = KqlQueryBuilder.PrepareScheduled(workspace, rule.Kql, systems, start, end, lookback);
                onPrepared?.Invoke(new(prepared.CreatePreview(), workspace.WorkspaceId, start, end, lookback));
                if (targets.Length > 0)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(QueryTimeoutSeconds));
                    var result = await runner.TryQueryAsync(prepared, timeout.Token);
                    if (result.Error is not null)
                        throw new InvalidOperationException(result.Error);
                    var table = result.Tables?.SingleOrDefault() ?? throw new InvalidOperationException("The query returned no result table.");
                    (values, eventDetails) = ScheduledQueryResultNormalizer.Normalize(table, rule.Scope, targets);
                }
            }
            catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
            {
                failure = exception;
                error = $"The scheduled query timed out after {QueryTimeoutSeconds} seconds.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = exception;
                error = Bounded(exception.Message);
            }
            if (error is not null)
            {
                values.Clear();
                eventDetails.Clear();
                logger.LogWarning(failure, "Scheduled KQL rule {RuleId}, workspace {WorkspaceKey} failed: {Error}", rule.Id, key, error);
            }
            evaluations.Add(new(key, targets, values, error, eventDetails));
        }
        return new(end, evaluations);
    }

    internal static string Bounded(string message) => message.Length <= 2048 ? message : message[..2048];

    internal static ScheduledQueryRule CopyDefinition(ScheduledQueryRule source) => new()
    {
        Id = source.Id, Revision = source.Revision, Name = source.Name, Enabled = source.Enabled, Scope = source.Scope,
        WorkspaceKey = source.WorkspaceKey, WorkspaceKeysJson = source.WorkspaceKeysJson,
        Domain = source.Domain, Site = source.Site, Role = source.Role,
        Computers = source.Computers, Kql = source.Kql, IntervalMinutes = source.IntervalMinutes,
        WindowMinutes = source.WindowMinutes, IngestionDelayMinutes = source.IngestionDelayMinutes,
        Comparison = source.Comparison, Threshold = source.Threshold, Severity = source.Severity,
        CategoryName = source.CategoryName
    };
}
