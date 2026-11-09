namespace MonitoringApp;

public enum AgentAnalysisJobValidationCode
{
    CreatedByInvalid,
    ExecutionDisabled,
    ExpiryInvalid,
    EvidenceRequired,
    ScopeUnsupported,
    ActorRequired
}

public sealed class AgentAnalysisJobValidationException(AgentAnalysisJobValidationCode code, string message)
    : InvalidOperationException(message)
{
    public AgentAnalysisJobValidationCode Code { get; } = code;
}
