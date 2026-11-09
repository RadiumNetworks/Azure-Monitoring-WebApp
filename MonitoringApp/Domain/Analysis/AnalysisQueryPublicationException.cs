using System.Text.Json;

namespace MonitoringApp;

public enum AnalysisQueryPublicationCode
{
    VersionUnavailable,
    SnapshotInvalid
}

public sealed class AnalysisQueryPublicationException(
    AnalysisQueryPublicationCode code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public AnalysisQueryPublicationCode Code { get; } = code;
}

public sealed class AnalysisQuerySnapshotJsonException(JsonException exception)
    : JsonException(exception.Message, exception.Path, exception.LineNumber, exception.BytePositionInLine, exception);
