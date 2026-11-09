namespace MonitoringApp;

public enum LogbookValidationCode
{
    UserRequired, UserLength, CommentRequired, CommentLength
}

public sealed class LogbookValidationException(LogbookValidationCode code, string message)
    : InvalidOperationException(message)
{
    public LogbookValidationCode Code { get; } = code;
}
