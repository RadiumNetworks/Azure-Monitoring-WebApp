using System.Diagnostics;
using Azure.Identity;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp;

public sealed class AnalysisQueryRunner(
    CrossTenantLogAnalyticsOptions options,
    ILogAnalyticsSecretProvider secretProvider,
    TargetWorkspaceResolver workspaceResolver,
    IAnalysisLogAnalyticsQuery? queryClient = null,
    ILogger<AnalysisQueryRunner>? logger = null)
{
    public const int MaxTableRows = KqlQueryLimits.MaxTableRows;
    public const int MaxTableResponseBytes = KqlQueryLimits.MaxTableResponseBytes;
    private const int MaxDiagnosticMessageLength = 2048;
    private readonly IAnalysisLogAnalyticsQuery queryClient = queryClient ?? new AzureAnalysisLogAnalyticsQuery();
    private readonly ILogger<AnalysisQueryRunner> logger = logger ?? NullLogger<AnalysisQueryRunner>.Instance;
    public TargetWorkspaceResolver WorkspaceResolver { get; } = workspaceResolver;

    public AnalysisQueryExecutionPreview CreateDraftExecutionPreview(
        AnalysisQueryDefinition definition,
        string workspaceKey,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        int? binMinutes = null,
        string groupBy = "SourceDSA") =>
        PrepareDraftExecution(definition, workspaceKey, systems, requestedMinutes, binMinutes, groupBy).CreatePreview();

    private PreparedKqlQuery PrepareDraftExecution(
        AnalysisQueryDefinition definition,
        string workspaceKey,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        int? binMinutes,
        string groupBy)
    {
        if (!options.Enabled)
            throw new AnalysisValidationException(AnalysisValidationCode.LiveDisabled, "Live Log Analytics is disabled.");
        AnalysisQueryValidator.RequireValidQuery(definition);
        AnalysisQueryValidator.ValidateExecutionLimits(definition, systems.Count, requestedMinutes);
        AnalysisQuerySelections.Validate(binMinutes == definition.DefaultBinMinutes ? null : binMinutes, groupBy);
        var workspace = options.Workspaces.SingleOrDefault(item =>
            item.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceRequired, "Select a configured Log Analytics workspace.");
        if (!definition.Workspaces.Any(item => item.Enabled &&
            item.WorkspaceKey.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)))
            throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceNotEnabled, "Enable the selected workspace for this query.");
        if (systems.Any(system => !WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain)
            .Workspace.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)))
            throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceRouteMismatch,
                "Every selected system must route to the selected workspace.");
        var names = systems.Select(system => system.Computer)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return KqlQueryBuilder.PrepareInteractive(workspace, definition.Kql, names,
            binMinutes ?? definition.DefaultBinMinutes, requestedMinutes, groupBy, definition.VisualizationType);
    }

    public async Task<AnalysisQueryResponse> ExecuteDraftAsync(
        AnalysisQueryDefinition definition,
        string workspaceKey,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        CancellationToken cancellationToken = default,
        int? binMinutes = null,
        string groupBy = "SourceDSA")
    {
        var prepared = PrepareDraftExecution(definition, workspaceKey, systems, requestedMinutes, binMinutes, groupBy);
        var textContext = QueryTextDisplayContext.Capture(definition);
        var snapshot = AnalysisQueryRegistry.CreateSnapshot(definition);
        var workspace = prepared.Workspace;
        var watch = Stopwatch.StartNew();
        var from = DateTimeOffset.UtcNow.AddMinutes(-requestedMinutes);
        var result = await TryQueryAsync(prepared, cancellationToken);
        if (result.Error is not null)
            throw new InvalidOperationException($"{workspace.DisplayName}: {result.Error}");
        var now = DateTimeOffset.UtcNow;
        return new(textContext.QueryId, snapshot.Name, textContext.Version, snapshot.Unit,
            from, now, now, watch.ElapsedMilliseconds, CreateSeries(result.Rows, workspace.DisplayName),
            result.Warning is null ? [] : [new(workspace.DisplayName, result.Warning) { Notice = result.WarningNotice }],
            prepared.VisualizationType, result.Tables, snapshot.Visualization,
            prepared.BinMinutes, snapshot.Scope) { StandardTextDisplayContext = textContext };
    }

    public IReadOnlyList<AnalysisQueryExecutionPreview> CreateExecutionPreviews(
        AnalysisQueryDefinition definition,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        int? binMinutes = null,
        string groupBy = "SourceDSA",
        string? workspaceKey = null,
        string? domain = null,
        string? site = null,
        string? role = null) =>
        PrepareExecutions(definition, systems, requestedMinutes, binMinutes, groupBy, workspaceKey, domain, site, role)
            .Select(query => query.CreatePreview(sortSystems: true))
            .OrderBy(preview => preview.WorkspaceDisplayName)
            .ToArray();

    private PreparedKqlQuery[] PrepareExecutions(
        AnalysisQueryDefinition definition,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes, int? binMinutes, string groupBy,
        string? workspaceKey, string? domain, string? site, string? role)
    {
        systems = FilterWorkspaceSystems(definition, systems, workspaceKey, domain, site, role);
        AnalysisQueryValidator.ValidateExecutionLimits(definition, systems.Count, requestedMinutes);
        AnalysisQuerySelections.Validate(binMinutes == definition.DefaultBinMinutes ? null : binMinutes, groupBy);
        return systems
            .Select(system => (
                System: system,
                Resolution: WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain)))
            .GroupBy(item => item.Resolution.Workspace.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var workspace = group.First().Resolution.Workspace;
                var names = group.Select(item => item.System.Computer)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return KqlQueryBuilder.PrepareInteractive(
                    workspace, definition.Kql, names, binMinutes ?? definition.DefaultBinMinutes,
                    requestedMinutes, groupBy, definition.VisualizationType);
            })
            .ToArray();
    }

    private IReadOnlyList<ComputerInventoryEntry> FilterWorkspaceSystems(
        AnalysisQueryDefinition definition,
        IReadOnlyList<ComputerInventoryEntry> systems, string? workspaceKey, string? domain, string? site, string? role)
    {
        if (definition.Scope == AnalysisQueryScopes.Workspace ||
            (string.IsNullOrEmpty(workspaceKey) && string.IsNullOrEmpty(domain) &&
             string.IsNullOrEmpty(site) && string.IsNullOrEmpty(role)))
            return systems;
        var workspace = string.IsNullOrEmpty(workspaceKey) ? null :
            options.Workspaces.SingleOrDefault(item => item.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceUnavailable,
                "The selected Log Analytics workspace is not configured.");
        var normalizedDomain = CrossTenantLogAnalyticsOptions.NormalizeDomain(domain);
        CrossTenantLogAnalyticsWorkspaceOptions? domainWorkspace = null;
        if (!string.IsNullOrEmpty(domain))
        {
            if (normalizedDomain.Length == 0)
                throw new AnalysisValidationException(AnalysisValidationCode.DomainUnavailable,
                    "The selected domain is not configured for the selected workspace.");
            domainWorkspace = options.Workspaces.SingleOrDefault(item =>
                (workspace is null || item.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)) &&
                item.Domains.Any(value => CrossTenantLogAnalyticsOptions.NormalizeDomain(value) == normalizedDomain))
                ?? systems.Where(system => CrossTenantLogAnalyticsOptions.NormalizeDomain(system.Domain) == normalizedDomain)
                    .Select(system => WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain).Workspace)
                    .FirstOrDefault(item => workspace is null || item.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new AnalysisValidationException(AnalysisValidationCode.DomainUnavailable,
                    "The selected domain is not configured for the selected workspace.");
        }
        var filtered = systems.Where(system =>
            (workspace is null || WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain)
                .Workspace.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)) &&
            (domainWorkspace is null || CrossTenantLogAnalyticsOptions.NormalizeDomain(system.Domain) == normalizedDomain)).ToArray();
        if (filtered.Length == 0)
        {
            if (domainWorkspace is not null)
                throw new AnalysisValidationException(AnalysisValidationCode.DomainSystemsMissing,
                    $"No selected systems belong to domain '{domainWorkspace.GetDomainDisplayName(normalizedDomain)}'. Select systems from this domain or choose All domains.",
                    domainWorkspace.GetDomainDisplayName(normalizedDomain));
            if (workspace is not null)
                throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceSystemsMissing,
                    $"No selected systems belong to workspace '{workspace.DisplayName}'. Select systems from this workspace or choose All workspaces.",
                    workspace.DisplayName);
        }
        var matching = filtered.Where(system =>
            (string.IsNullOrEmpty(site) || string.Equals(system.Site, site, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(role) || string.Equals(system.Role, role, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matching.Length == 0 && (!string.IsNullOrEmpty(site) || !string.IsNullOrEmpty(role)))
            throw new AnalysisValidationException(AnalysisValidationCode.FiltersNoMatch,
                "No selected systems match the selected site and role filters. Select matching systems or choose All sites / All roles.");
        return matching;
    }

    public async Task<AnalysisQueryResponse> ExecuteAsync(
        AnalysisQueryDefinition definition,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        CancellationToken cancellationToken = default,
        int? binMinutes = null,
        string groupBy = "SourceDSA",
        string? workspaceKey = null,
        string? domain = null,
        string? site = null,
        string? role = null)
    {
        var prepared = PrepareExecutions(
            definition, systems, requestedMinutes, binMinutes, groupBy, workspaceKey, domain, site, role);
        var textContext = QueryTextDisplayContext.Capture(definition);
        var snapshot = AnalysisQueryRegistry.CreateSnapshot(definition);
        var minutes = requestedMinutes;
        var enabledWorkspaces = definition.Workspaces
            .Where(item => item.Enabled && item.LastValidationStatus == AnalysisQueryValidationStatuses.Succeeded)
            .Select(item => item.WorkspaceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (prepared.Any(query => !enabledWorkspaces.Contains(query.Workspace.Key)))
            throw new AnalysisValidationException(AnalysisValidationCode.QueryNotValidated,
                "The published query is not validated for every selected system workspace.");

        var watch = Stopwatch.StartNew();
        var series = new List<AnalysisQuerySeries>();
        var tables = new List<AnalysisQueryTable>();
        var succeeded = 0;
        var warnings = new List<AnalysisQueryWarning>();
        foreach (var query in prepared)
        {
            var workspace = query.Workspace;
            var result = await TryQueryAsync(query, cancellationToken);
            if (result.Error is not null)
            {
                warnings.Add(new AnalysisQueryWarning(workspace.DisplayName, result.Error) { Notice = result.ErrorNotice });
                continue;
            }
            series.AddRange(CreateSeries(result.Rows, workspace.DisplayName));
            tables.AddRange(result.Tables ?? []);
            if (result.Warning is not null)
                warnings.Add(new(workspace.DisplayName, result.Warning) { Notice = result.WarningNotice });
            succeeded++;
        }

        if (succeeded == 0 && warnings.Count > 0)
            throw new InvalidOperationException(string.Join(" ", warnings.Select(warning => $"{warning.Workspace}: {warning.Message}")));

        var now = DateTimeOffset.UtcNow;
        return new AnalysisQueryResponse(
            textContext.QueryId,
            snapshot.Name,
            textContext.Version,
            snapshot.Unit,
            now.AddMinutes(-minutes),
            now,
            now,
            watch.ElapsedMilliseconds,
            series,
            warnings,
            prepared[0].VisualizationType,
            tables,
            snapshot.Visualization,
            prepared[0].BinMinutes,
            snapshot.Scope) { StandardTextDisplayContext = textContext };
    }

    public Task<AnalysisQueryRunResult> TryQueryAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string kql,
        IReadOnlyList<string> systems,
        int timeRangeMinutes,
        int binMinutes,
        CancellationToken cancellationToken,
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart,
        string groupBy = "SourceDSA") =>
        TryQueryCoreAsync(workspace, systems.Count, timeRangeMinutes,
            () => KqlQueryBuilder.PrepareInteractive(workspace, kql, systems, binMinutes,
                timeRangeMinutes, groupBy, visualizationType), cancellationToken);

    internal Task<AnalysisQueryRunResult> TryQueryAsync(PreparedKqlQuery query, CancellationToken cancellationToken) =>
        TryQueryCoreAsync(query.Workspace, query.Systems.Count, query.ApiTimeRangeMinutes, () => query, cancellationToken);

    private async Task<AnalysisQueryRunResult> TryQueryCoreAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, int systemCount, int timeRangeMinutes,
        Func<PreparedKqlQuery> prepare, CancellationToken cancellationToken)
    {
        try
        {
            var secret = await secretProvider.GetClientSecretAsync(cancellationToken);
            var query = prepare();
            var response = await queryClient.QueryAsync(
                workspace, options.ApplicationClientId, secret,
                query.EffectiveKql,
                timeRangeMinutes, cancellationToken);
            if (response.Error is not null)
            {
                var diagnosticId = Guid.NewGuid().ToString("N");
                var details = LimitDiagnostic(response.Error.Message);
                var loggingFailure = LogFailure(diagnosticId, $"{response.Error.Code}: {details}");
                return new([], $"Query failed: {response.Error.Code}. {details} Partial results were not used. Diagnostic ID: {diagnosticId}.{loggingFailure}")
                { ErrorNotice = new(AnalysisQueryNoticeCode.PartialResultsRejected, diagnosticId,
                    DiagnosticLoggingFailed: loggingFailure is not null) };
            }
            return query.Profile == KqlExecutionProfile.Scheduled
                ? KqlResultNormalizer.NormalizeScheduled(response.Table, workspace.DisplayName)
                : KqlResultNormalizer.Normalize(response.Table, workspace.DisplayName, query.VisualizationType);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(exception, "The Log Analytics request timed out. Try a shorter time range or fewer systems.",
                AnalysisQueryNoticeCode.RequestTimeout);
        }
        catch (Azure.RequestFailedException exception)
        {
            return Failure(exception,
                $"Azure HTTP {exception.Status}" +
                (string.IsNullOrWhiteSpace(exception.ErrorCode) ? "." : $" ({exception.ErrorCode})."),
                AnalysisQueryNoticeCode.AzureRequestFailed, exception.Status);
        }
        catch (Exception exception) when (exception is AuthenticationFailedException or InvalidOperationException)
        {
            return Failure(exception, exception is AuthenticationFailedException
                ? "Log Analytics authentication failed."
                : exception.Message, exception is AuthenticationFailedException
                    ? AnalysisQueryNoticeCode.AuthenticationFailed : AnalysisQueryNoticeCode.QueryFailed);
        }

        AnalysisQueryRunResult Failure(Exception exception, string message, AnalysisQueryNoticeCode code, int? httpStatus = null)
        {
            var diagnosticId = Guid.NewGuid().ToString("N");
            var loggingFailure = LogFailure(diagnosticId, $"{exception.GetType().Name}: {LimitDiagnostic(exception.Message)}");
            return new([], $"Query failed: {LimitDiagnostic(message)} Diagnostic ID: {diagnosticId}. See application logs for details.{loggingFailure}")
            { ErrorNotice = new(code, diagnosticId, httpStatus, loggingFailure is not null) };
        }

        string? LogFailure(string diagnosticId, string details)
        {
            try
            {
                logger.LogWarning(
                    "Log Analytics query failure {DiagnosticId}. Workspace={WorkspaceKey}, Systems={SystemCount}, Minutes={Minutes}, Details={Details}",
                    diagnosticId, workspace.Key, systemCount, timeRangeMinutes, LimitDiagnostic(details));
                return null;
            }
            catch (AggregateException exception) when (exception.Flatten().InnerExceptions.Any(inner => inner is OutOfMemoryException))
            {
                return " Diagnostic logging also failed due to insufficient memory. Restart the application after deploying the memory limits.";
            }
        }
    }

    private static string LimitDiagnostic(string? message) =>
        message is null ? "No additional error details were returned." :
        message.Length <= MaxDiagnosticMessageLength ? message : message[..MaxDiagnosticMessageLength] + "...";

    public static string BuildEffectiveKql(
        string kql,
        IReadOnlyList<string> systems,
        int binMinutes,
        int timeRangeMinutes = 1440,
        string groupBy = "SourceDSA",
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart) =>
        KqlQueryBuilder.BuildEffectiveKql(kql, systems, binMinutes, timeRangeMinutes, groupBy, visualizationType);

    public static AnalysisQueryRunResult NormalizeResult(LogsTable table, string workspace, string visualizationType)
        => KqlResultNormalizer.Normalize(table, workspace, visualizationType);

    public static AnalysisQueryRunResult NormalizeResult(AnalysisQueryTable table, string visualizationType)
        => KqlResultNormalizer.Normalize(table, visualizationType);

    private static AnalysisQuerySeries[] CreateSeries(
        IReadOnlyList<AnalysisQueryNormalizedRow> rows,
        string workspace) => rows.GroupBy(row => (row.Series, row.Dimension))
        .Select(group => new AnalysisQuerySeries(
            group.Key.Series, group.Key.Dimension, workspace,
            group.OrderBy(row => row.Timestamp)
                .Select(row => new AnalysisQueryPoint(row.Timestamp, row.Value, row.Label)).ToArray()))
        .ToArray();

    public sealed class AnalysisQueryRawRow
    {
        public DateTimeOffset Timestamp { get; set; }
        public string Series { get; set; } = string.Empty;
        public double Value { get; set; }
        public string? Label { get; set; }
        public string? Dimension { get; set; }
    }
}
