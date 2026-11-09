using System.Text.Json;

namespace MonitoringApp;

public enum ScheduledQueryDataCode
{
    WorkspaceSelectionInvalid,
    WorkspaceResultsInvalid
}

public sealed class ScheduledQueryDataException(ScheduledQueryDataCode code, string message)
    : InvalidOperationException(message)
{
    public ScheduledQueryDataCode Code { get; } = code;
}

public sealed class ScheduledQueryDataJsonException(ScheduledQueryDataCode code, JsonException exception)
    : JsonException(exception.Message, exception.Path, exception.LineNumber, exception.BytePositionInLine, exception)
{
    public ScheduledQueryDataCode Code { get; } = code;
}

internal static class ScheduledQueryData
{
    internal static T Read<T>(string json, ScheduledQueryDataCode code, string nullMessage)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? throw new ScheduledQueryDataException(code, nullMessage);
        }
        catch (JsonException exception) { throw new ScheduledQueryDataJsonException(code, exception); }
    }
}
