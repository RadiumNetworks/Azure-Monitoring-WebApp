using System.Security.Claims;

namespace MonitoringApp;

public sealed class AgentAnalysisAccessOptions
{
    public const string SectionName = "AgentAnalysisAccess";

    public string[] AllowedTenantIds { get; set; } = [];
    public string[] AllowedSubscriptionIds { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        ValidateValues(AllowedTenantIds, nameof(AllowedTenantIds), errors);
        ValidateValues(AllowedSubscriptionIds, nameof(AllowedSubscriptionIds), errors);
        return errors;
    }

    private static void ValidateValues(
        IEnumerable<string> values,
        string propertyName,
        ICollection<string> errors)
    {
        var normalized = values.Select(value => value?.Trim() ?? string.Empty).ToArray();
        if (normalized.Any(value => value.Length is 0 or > 128))
        {
            errors.Add($"{propertyName} entries must contain between 1 and 128 characters.");
        }

        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            errors.Add($"{propertyName} must not contain duplicates.");
        }
    }
}

public sealed class AgentAnalysisScopeDeniedException(string message) : InvalidOperationException(message);

public sealed class AgentAnalysisScopePolicy(AgentAnalysisAccessOptions options)
{
    private static readonly string[] TenantClaimTypes =
    [
        "tid",
        "http://schemas.microsoft.com/identity/claims/tenantid"
    ];

    private readonly HashSet<string> allowedTenantIds = new(
        options.AllowedTenantIds.Select(value => value.Trim()),
        StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> allowedSubscriptionIds = new(
        options.AllowedSubscriptionIds.Select(value => value.Trim()),
        StringComparer.OrdinalIgnoreCase);

    public void EnsureAllowed(
        ClaimsPrincipal user,
        bool openAuthentication,
        IncidentAnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!openAuthentication && allowedTenantIds.Count > 0)
        {
            var tenantId = TenantClaimTypes
                .Select(user.FindFirstValue)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (tenantId is null || !allowedTenantIds.Contains(tenantId))
            {
                throw new AgentAnalysisScopeDeniedException(
                    "The authenticated tenant is not allowed to start agent analysis jobs.");
            }
        }

        var subscriptions = snapshot.PrimaryEvidence
            .Select(evidence => evidence.SubscriptionId.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (subscriptions.Length == 0)
        {
            throw new AgentAnalysisScopeDeniedException(
                "Primary evidence must identify at least one subscription.");
        }

        if (allowedSubscriptionIds.Count > 0 &&
            subscriptions.Any(subscription => !allowedSubscriptionIds.Contains(subscription)))
        {
            throw new AgentAnalysisScopeDeniedException(
                "Primary evidence contains a subscription outside the configured analysis boundary.");
        }

        if (snapshot.Scope.Type == IncidentAnalysisScopeType.Target &&
            (subscriptions.Length != 1 || snapshot.PrimaryEvidence.Any(evidence =>
                !evidence.Target.Equals(snapshot.Scope.Value, StringComparison.OrdinalIgnoreCase))))
        {
            throw new AgentAnalysisScopeDeniedException(
                "Target analysis requires matching primary evidence from exactly one subscription.");
        }
    }
}

public sealed class AgentAnalysisRequestValidationException(
    IReadOnlyDictionary<string, string[]> errors) : InvalidOperationException("The analysis request is invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class AgentAnalysisJobService(
    IncidentAnalysisSnapshotService snapshotService,
    AgentAnalysisOrchestrator orchestrator,
    AgentAnalysisJobStore jobStore,
    AgentAnalysisReviewStore reviewStore,
    AgentAnalysisScopePolicy scopePolicy,
    AlertGraphOptions graphOptions,
    AgentAnalysisContextOptions contextOptions,
    TimeProvider timeProvider,
    ApplicationAuthenticationOptions authentication)
{
    public AgentAnalysisExecutionState ExecutionState => orchestrator.ExecutionState;

    public async Task<AgentAnalysisJobStartResponse> StartAsync(
        AgentAnalysisJobStartRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var validationErrors = AgentAnalysisJobApi.Validate(request);
        if (validationErrors.Count > 0)
        {
            throw new AgentAnalysisRequestValidationException(validationErrors);
        }

        var generatedAt = timeProvider.GetUtcNow();
        var scope = new IncidentAnalysisScope(
            AgentAnalysisJobApi.ParseScopeType(request.ScopeType),
            request.ScopeValue.Trim());
        var snapshot = await snapshotService.BuildAsync(
            request.PrimaryEventIds!,
            scope,
            graphOptions.DefaultLayer1,
            graphOptions.DefaultLayer2,
            graphOptions.DefaultLayer3,
            generatedAt,
            new IncidentAnalysisSnapshotOptions(
                SystemHistoryDays: request.SystemHistoryDays,
                ContextHours: request.ContextHours,
                IncludeSystemHistory: request.IncludeSystemHistory,
                IncludeRelatedContext: request.IncludeRelatedContext,
                InventoryCorrelationLayers: contextOptions.InventoryCorrelationLayers),
            cancellationToken);

        scopePolicy.EnsureAllowed(user, authentication.IsOpen, snapshot);
        var handoff = orchestrator.Prepare(snapshot);
        var actor = AgentAnalysisJobApi.GetActor(user, authentication.IsOpen);
        var job = await jobStore.CreateAsync(
            new AgentAnalysisJobCreateRequest(
                snapshot,
                orchestrator.ExecutionState,
                actor,
                generatedAt.Add(AgentAnalysisJobApi.JobRetention)),
            cancellationToken);

        return new AgentAnalysisJobStartResponse(
            AgentAnalysisJobResponse.FromJob(job),
            handoff.Prompt);
    }

    public async Task<IReadOnlyList<AgentAnalysisJobResponse>> ListAccessibleAsync(
        ClaimsPrincipal user,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var actor = AgentAnalysisJobApi.GetActor(user, authentication.IsOpen);
        var includeAll = authentication.IsOpen || user.IsInRole(SqlAuthenticationRoles.Admin);
        var jobs = await jobStore.ListAsync(actor, includeAll, limit, cancellationToken);
        return jobs.Select(AgentAnalysisJobResponse.FromJob).ToArray();
    }

    public async Task<AgentAnalysisJob?> GetAccessibleAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var job = await jobStore.GetAsync(id, cancellationToken);
        return job is not null && AgentAnalysisJobApi.CanAccess(job, user, authentication.IsOpen)
            ? job
            : null;
    }

    public async Task<IReadOnlyList<AgentAnalysisReviewEvent>> ListReviewsAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var job = await GetAccessibleAsync(id, user, cancellationToken);
        if (job is null)
        {
            throw new KeyNotFoundException($"Agent analysis job '{id}' was not found.");
        }

        return await reviewStore.ListAsync(id, cancellationToken);
    }

    public async Task<AgentAnalysisReviewEvent> ReviewAsync(
        Guid id,
        ClaimsPrincipal user,
        string action,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var job = await GetAccessibleAsync(id, user, cancellationToken);
        if (job is null)
        {
            throw new KeyNotFoundException($"Agent analysis job '{id}' was not found.");
        }

        return await reviewStore.AddAsync(
            job,
            AgentAnalysisJobApi.GetActor(user, authentication.IsOpen),
            action,
            comment,
            cancellationToken);
    }
}

public sealed class AgentAnalysisRetentionService(
    AgentAnalysisJobStore jobStore,
    TimeProvider timeProvider,
    ILogger<AgentAnalysisRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), timeProvider);
        do
        {
            try
            {
                var deleted = await jobStore.DeleteExpiredAsync(
                    timeProvider.GetUtcNow(),
                    stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Deleted {DeletedJobCount} expired agent analysis jobs.",
                        deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Agent analysis retention cleanup failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}