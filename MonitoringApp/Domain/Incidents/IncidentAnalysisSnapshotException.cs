namespace MonitoringApp;

public enum IncidentAnalysisSnapshotCode
{
    SystemHistoryDaysInvalid,
    ContextHoursInvalid,
    AlertLimitInvalid,
    CharacterLimitInvalid,
    CorrelationLayersInvalid,
    PrimaryIdsRequired,
    PrimaryEventsMissing,
    PrimaryEvidenceRequired,
    PrimaryAlertLimitExceeded,
    PrimaryCharacterLimitExceeded
}

public sealed class IncidentAnalysisSnapshotRangeException(IncidentAnalysisSnapshotCode code, string paramName)
    : ArgumentOutOfRangeException(paramName)
{
    public IncidentAnalysisSnapshotCode Code { get; } = code;
}

public sealed class IncidentAnalysisSnapshotArgumentException(IncidentAnalysisSnapshotCode code, string message, string paramName)
    : ArgumentException(message, paramName)
{
    public IncidentAnalysisSnapshotCode Code { get; } = code;
}

public sealed class IncidentAnalysisSnapshotException(IncidentAnalysisSnapshotCode code, string message)
    : InvalidOperationException(message)
{
    public IncidentAnalysisSnapshotCode Code { get; } = code;
}
