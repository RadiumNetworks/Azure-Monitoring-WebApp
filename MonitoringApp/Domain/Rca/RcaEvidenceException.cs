using System.Text.Json;

namespace MonitoringApp;

public sealed class RcaEvidenceException(Guid rcaId, string message) : InvalidOperationException(message)
{
    public Guid RcaId { get; } = rcaId;
}

public sealed class RcaEvidenceJsonException(Guid rcaId, JsonException exception)
    : JsonException(exception.Message, exception.Path, exception.LineNumber, exception.BytePositionInLine, exception)
{
    public Guid RcaId { get; } = rcaId;
}
