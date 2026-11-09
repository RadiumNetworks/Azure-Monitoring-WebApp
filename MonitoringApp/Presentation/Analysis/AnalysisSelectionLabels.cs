namespace MonitoringApp;

public static class AnalysisSelectionLabels
{
    public static string TimeRange(UiText text, int minutes) => minutes switch
    {
        2880 => text.Format("AnalysisSelections.Days", 2),
        10080 => text.Format("AnalysisSelections.Days", 7),
        43200 => text.Format("AnalysisSelections.Days", 30),
        _ => AnalysisQuerySelections.TimeRangeLabel(minutes)
    };
}
