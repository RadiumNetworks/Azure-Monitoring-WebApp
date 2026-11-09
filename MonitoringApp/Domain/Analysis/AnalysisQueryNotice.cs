namespace MonitoringApp;

public enum AnalysisQueryNoticeCode
{
    TableRowLimit,
    PartialResultsRejected,
    RequestTimeout,
    AzureRequestFailed,
    AuthenticationFailed,
    QueryFailed,
    UnsupportedVisualization,
    InconsistentRows,
    InvalidStatColumns,
    MissingColumns,
    InvalidSeriesValues,
    InvalidTimestamp
}

public sealed record AnalysisQueryNotice(
    AnalysisQueryNoticeCode Code,
    string? DiagnosticId = null,
    int? HttpStatus = null,
    bool DiagnosticLoggingFailed = false);
