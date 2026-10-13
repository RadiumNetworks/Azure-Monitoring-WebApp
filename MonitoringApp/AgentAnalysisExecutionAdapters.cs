namespace MonitoringApp;

public sealed record AgentAnalysisAdapterRequest(
    Guid AnalysisId,
    int ResultVersion,
    string SnapshotVersion,
    string ResultContractVersion,
    string SnapshotJson);

public sealed record AgentAnalysisAttachmentMetadata(
    string FileName,
    string ContentType,
    Uri DownloadUri,
    DateTimeOffset ExpiresAt);

public sealed record AgentAnalysisTeamsTransportRequest(
    Guid AnalysisId,
    int ResultVersion,
    IReadOnlyList<AgentTeamsSummaryBullet> Summary,
    Uri PortalUri,
    AgentAnalysisAttachmentMetadata Attachment)
{
    public static AgentAnalysisTeamsTransportRequest Create(
        AgentAnalysisJob job,
        AgentAnalysisResult result,
        Uri portalUri,
        Uri attachmentUri)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        EnsureHttps(portalUri, nameof(portalUri));
        EnsureHttps(attachmentUri, nameof(attachmentUri));

        if (job.Status != AgentAnalysisJobStatuses.Completed ||
            result.AnalysisId != job.AnalysisId ||
            result.ResultVersion != job.ResultVersion)
        {
            throw new InvalidOperationException(
                "Teams transport requires the matching completed analysis result.");
        }

        return new AgentAnalysisTeamsTransportRequest(
            job.AnalysisId,
            job.ResultVersion,
            result.TeamsSummary,
            portalUri,
            new AgentAnalysisAttachmentMetadata(
                AgentAnalysisMarkdownRenderer.CreateFileName(job),
                "text/markdown; charset=utf-8",
                attachmentUri,
                job.ExpiresAt));
    }

    private static void EnsureHttps(Uri uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Teams transport links must use absolute HTTPS URLs.", parameterName);
        }
    }
}

public interface IAgentAnalysisExecutionAdapter
{
    string Mode { get; }

    Task<AgentAnalysisResult> ExecuteAsync(
        AgentAnalysisAdapterRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DisabledTeamsWorkflowAdapter : IAgentAnalysisExecutionAdapter
{
    public string Mode => AgentAnalysisExecutionModes.TeamsWorkflow;

    public Task<AgentAnalysisResult> ExecuteAsync(
        AgentAnalysisAdapterRequest request,
        CancellationToken cancellationToken = default) =>
        throw new AgentAnalysisUnavailableException(
            "Teams workflow execution is disabled until its endpoint, security review, and transport have been approved.");
}

public sealed class DisabledApiAnalysisAdapter : IAgentAnalysisExecutionAdapter
{
    public string Mode => AgentAnalysisExecutionModes.Api;

    public Task<AgentAnalysisResult> ExecuteAsync(
        AgentAnalysisAdapterRequest request,
        CancellationToken cancellationToken = default) =>
        throw new AgentAnalysisUnavailableException(
            "API execution is disabled until its provider, endpoint, security review, and data processing have been approved.");
}