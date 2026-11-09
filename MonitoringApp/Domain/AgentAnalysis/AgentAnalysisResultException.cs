namespace MonitoringApp;

public enum AgentAnalysisResultCode
{
    NullResponse,
    SchemaMismatch,
    ValidationFailed
}

public sealed class AgentAnalysisResultException(AgentAnalysisResultCode code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public AgentAnalysisResultCode Code { get; } = code;
}
