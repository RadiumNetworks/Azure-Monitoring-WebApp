namespace MonitoringApp;

public static class AnalysisQueryWarningPresentation
{
    public static string Message(AnalysisQueryWarning warning, UiText text)
    {
        if (warning.Notice is not { } notice) return warning.Message;
        var key = notice.Code switch
        {
            AnalysisQueryNoticeCode.TableRowLimit or AnalysisQueryNoticeCode.PartialResultsRejected or
            AnalysisQueryNoticeCode.RequestTimeout or AnalysisQueryNoticeCode.AzureRequestFailed or
            AnalysisQueryNoticeCode.AuthenticationFailed or AnalysisQueryNoticeCode.QueryFailed or
            AnalysisQueryNoticeCode.UnsupportedVisualization or AnalysisQueryNoticeCode.InconsistentRows or
            AnalysisQueryNoticeCode.InvalidStatColumns or AnalysisQueryNoticeCode.MissingColumns or
            AnalysisQueryNoticeCode.InvalidSeriesValues or AnalysisQueryNoticeCode.InvalidTimestamp => $"AnalysisWarnings.{notice.Code}",
            _ => null
        };
        if (key is null) return text["AnalysisWarnings.Unavailable"];
        var message = notice.Code switch
        {
            AnalysisQueryNoticeCode.TableRowLimit => text.Format(key, AnalysisQueryRunner.MaxTableRows),
            AnalysisQueryNoticeCode.AzureRequestFailed => notice.HttpStatus is { } status
                ? text.Format(key, status) : text["AnalysisWarnings.Unavailable"],
            _ => text[key]
        };
        if (notice.DiagnosticId is not null)
        {
            if (!Guid.TryParseExact(notice.DiagnosticId, "N", out _)) return text["AnalysisWarnings.Unavailable"];
            message = text.Format("AnalysisWarnings.WithDiagnostic", message, notice.DiagnosticId);
        }
        return notice.DiagnosticLoggingFailed ? text.Format("AnalysisWarnings.LoggingFailed", message) : message;
    }
}
