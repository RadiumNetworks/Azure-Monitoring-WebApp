using System.Security.Claims;

namespace MonitoringApp;

public sealed record AgentAnalysisJobStartRequest(
    IReadOnlyList<Guid>? PrimaryEventIds,
    string ScopeType = nameof(IncidentAnalysisScopeType.Incident),
    string ScopeValue = "",
    int SystemHistoryDays = 7,
    int ContextHours = 24,
    bool IncludeSystemHistory = true,
    bool IncludeRelatedContext = true);

public sealed record AgentAnalysisJobCancelRequest(int ExpectedRevision, string? Reason = null);

public sealed record AgentAnalysisJobStartResponse(
    AgentAnalysisJobResponse Job,
    string ManualPrompt);

public sealed record AgentAnalysisJobResponse(
    Guid Id,
    Guid AnalysisId,
    int ResultVersion,
    string Status,
    string StatusReason,
    int Revision,
    string Mode,
    string ExecutionProfile,
    string PromptVersion,
    string SnapshotVersion,
    string ResultContractVersion,
    string ScopeType,
    string ScopeValue,
    DateTimeOffset DataFrom,
    DateTimeOffset DataTo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    string ErrorCode,
    string ErrorMessage)
{
    public static AgentAnalysisJobResponse FromJob(AgentAnalysisJob job) => new(
        job.Id,
        job.AnalysisId,
        job.ResultVersion,
        job.Status,
        job.StatusReason,
        job.Revision,
        job.Mode,
        job.ExecutionProfile,
        job.PromptVersion,
        job.SnapshotVersion,
        job.ResultContractVersion,
        job.ScopeType,
        job.ScopeValue,
        job.DataFrom,
        job.DataTo,
        job.CreatedAt,
        job.UpdatedAt,
        job.StartedAt,
        job.CompletedAt,
        job.ExpiresAt,
        job.ErrorCode,
        job.ErrorMessage);
}

public static class AgentAnalysisJobApi
{
    public const int MaxPrimaryEvents = 100;
    public const int MaxSystemHistoryDays = 30;
    public const int MaxContextHours = 24;
    public static readonly TimeSpan JobRetention = TimeSpan.FromDays(30);

    public static IReadOnlyDictionary<string, string[]> Validate(AgentAnalysisJobStartRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors["request"] = ["A request body is required."];
            return errors;
        }

        var eventIds = request.PrimaryEventIds ?? [];
        if (eventIds.Count is < 1 or > MaxPrimaryEvents)
        {
            errors[nameof(request.PrimaryEventIds)] =
                [$"Between 1 and {MaxPrimaryEvents} primary event IDs are required."];
        }
        else if (eventIds.Any(id => id == Guid.Empty) || eventIds.Distinct().Count() != eventIds.Count)
        {
            errors[nameof(request.PrimaryEventIds)] = ["Primary event IDs must be non-empty and unique."];
        }

        if (!TryParseScopeType(request.ScopeType, out var scopeType) ||
            scopeType is not IncidentAnalysisScopeType.Incident and
                not IncidentAnalysisScopeType.Target)
        {
            errors[nameof(request.ScopeType)] =
                ["ScopeType must be Incident or Target until graph-scope authorization is available."];
        }

        if (string.IsNullOrWhiteSpace(request.ScopeValue) || request.ScopeValue.Trim().Length > 512)
        {
            errors[nameof(request.ScopeValue)] = ["ScopeValue is required and may contain at most 512 characters."];
        }

        if (request.SystemHistoryDays is < 1 or > MaxSystemHistoryDays)
        {
            errors[nameof(request.SystemHistoryDays)] =
                [$"SystemHistoryDays must be between 1 and {MaxSystemHistoryDays}."];
        }

        if (request.ContextHours is < 1 or > MaxContextHours)
        {
            errors[nameof(request.ContextHours)] =
                [$"ContextHours must be between 1 and {MaxContextHours}."];
        }

        return errors;
    }

    public static IncidentAnalysisScopeType ParseScopeType(string value) =>
        TryParseScopeType(value, out var scopeType)
            ? scopeType
            : throw new InvalidOperationException("The analysis scope type is not supported.");

    public static string GetActor(ClaimsPrincipal user, bool openAuthentication)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (openAuthentication)
        {
            return "open-mode-operator";
        }

        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.Identity?.Name
            ?? throw new InvalidOperationException("The authenticated user has no stable identifier.");
    }

    public static bool CanAccess(AgentAnalysisJob job, ClaimsPrincipal user, bool openAuthentication)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(user);
        return openAuthentication ||
            user.IsInRole(SqlAuthenticationRoles.Admin) ||
            job.CreatedBy.Equals(GetActor(user, false), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseScopeType(string? value, out IncidentAnalysisScopeType scopeType) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out scopeType) && Enum.IsDefined(scopeType);
}
