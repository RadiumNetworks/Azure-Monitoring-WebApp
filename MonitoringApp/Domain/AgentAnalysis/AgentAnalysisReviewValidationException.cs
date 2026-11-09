namespace MonitoringApp;

public enum AgentAnalysisReviewValidationCode
{
    ResultRequired,
    ActionInvalid,
    ActorInvalid,
    CommentTooLong,
    CommentRequired
}

public sealed class AgentAnalysisReviewValidationException(AgentAnalysisReviewValidationCode code, string message)
    : InvalidOperationException(message)
{
    public AgentAnalysisReviewValidationCode Code { get; } = code;
}
