namespace MonitoringApp;

public static class AgentAnalysisJobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Queued, Running, Completed, Failed, Cancelled, Expired],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(
        [Completed, Failed, Cancelled, Expired],
        StringComparer.Ordinal);
}

public static class AgentAnalysisDeliveryStatuses
{
    public const string NotRequested = "NotRequested";
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public sealed class AgentAnalysisJob
{
    public Guid Id { get; set; }
    public Guid AnalysisId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public int ResultVersion { get; set; }
    public string Status { get; set; } = AgentAnalysisJobStatuses.Queued;
    public string StatusReason { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Mode { get; set; } = AgentAnalysisExecutionModes.CopilotPrompt;
    public string ExecutionProfile { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string SnapshotVersion { get; set; } = string.Empty;
    public string ResultContractVersion { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeValue { get; set; } = string.Empty;
    public string PrimaryEventIdsJson { get; set; } = "[]";
    public string SnapshotJson { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public string TeamsSummaryJson { get; set; } = string.Empty;
    public string DetailedAnalysisJson { get; set; } = string.Empty;
    public string SourceIdsJson { get; set; } = "[]";
    public DateTimeOffset? ResultStoredAt { get; set; }
    public string TeamsDeliveryStatus { get; set; } = AgentAnalysisDeliveryStatuses.NotRequested;
    public int? TeamsDeliveredResultVersion { get; set; }
    public DateTimeOffset? TeamsDeliveredAt { get; set; }
    public DateTimeOffset SnapshotGeneratedAt { get; set; }
    public DateTimeOffset DataFrom { get; set; }
    public DateTimeOffset DataTo { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public static class AgentAnalysisJobStateMachine
{
    public static void Transition(
        AgentAnalysisJob job,
        string targetStatus,
        DateTimeOffset changedAt,
        string? reason = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!AgentAnalysisJobStatuses.All.Contains(targetStatus))
        {
            throw new InvalidOperationException($"Unknown agent analysis job status '{targetStatus}'.");
        }

        if (!CanTransition(job.Status, targetStatus))
        {
            throw new InvalidOperationException(
                $"Agent analysis job cannot transition from '{job.Status}' to '{targetStatus}'.");
        }

        if (changedAt < job.CreatedAt || changedAt < job.UpdatedAt)
        {
            throw new InvalidOperationException("Agent analysis job transition time cannot move backwards.");
        }

        if (targetStatus == AgentAnalysisJobStatuses.Failed && string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new InvalidOperationException("A failed agent analysis job requires an error message.");
        }

        job.Status = targetStatus;
        job.StatusReason = Normalize(reason, 1024);
        job.ErrorCode = targetStatus == AgentAnalysisJobStatuses.Failed
            ? Normalize(errorCode, 128)
            : string.Empty;
        job.ErrorMessage = targetStatus == AgentAnalysisJobStatuses.Failed
            ? Normalize(errorMessage, 4000)
            : string.Empty;
        job.UpdatedAt = changedAt;
        job.Revision++;

        if (targetStatus == AgentAnalysisJobStatuses.Running)
        {
            job.StartedAt = changedAt;
        }

        if (AgentAnalysisJobStatuses.Terminal.Contains(targetStatus))
        {
            job.CompletedAt = changedAt;
        }
    }

    public static bool CanTransition(string currentStatus, string targetStatus) => currentStatus switch
    {
        AgentAnalysisJobStatuses.Queued => targetStatus is
            AgentAnalysisJobStatuses.Running or
            AgentAnalysisJobStatuses.Cancelled or
            AgentAnalysisJobStatuses.Expired,
        AgentAnalysisJobStatuses.Running => targetStatus is
            AgentAnalysisJobStatuses.Completed or
            AgentAnalysisJobStatuses.Failed or
            AgentAnalysisJobStatuses.Cancelled or
            AgentAnalysisJobStatuses.Expired,
        _ => false
    };

    private static string Normalize(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}