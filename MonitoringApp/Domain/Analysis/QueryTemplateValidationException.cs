namespace MonitoringApp;

public enum QueryTemplateValidationCode
{
    InputRequired,
    ImportTooLarge,
    NullTemplate,
    InvalidJson
}

public sealed class QueryTemplateValidationException(
    QueryTemplateValidationCode code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public QueryTemplateValidationCode Code { get; } = code;
}
