using System.Text.Json;

namespace MonitoringApp;

internal static class KqlQueryLimits
{
    internal const int MaxTableRows = 1000;
    internal const int MaxTableResponseBytes = 4 * 1024 * 1024;
    internal const int MaxScheduledTargets = 1000;
    internal const int MaxScheduledResponseBytes = 1024 * 1024;
}

internal static class KqlQueryBuilder
{
    internal static PreparedKqlQuery PrepareInteractive(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string kql, IReadOnlyList<string> systems, int binMinutes, int timeRangeMinutes,
        string groupBy = "SourceDSA",
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart) =>
        Prepare(workspace, kql, systems, binMinutes, timeRangeMinutes, groupBy,
            visualizationType, KqlExecutionProfile.Interactive);

    internal static PreparedKqlQuery PrepareScheduled(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string kql, IReadOnlyList<string> systems, DateTime windowStartUtc, DateTime windowEndUtc,
        int apiTimeRangeMinutes)
    {
        var wrapped = $$"""
            set truncationmaxsize={{KqlQueryLimits.MaxScheduledResponseBytes}};
            let _WindowStart = datetime({{windowStartUtc:O}});
            let _WindowEnd = datetime({{windowEndUtc:O}});
            let _ScheduledQueryResult = () {
            {{kql}}
            };
            _ScheduledQueryResult()
            | project Series, Value,
                      EventId = column_ifexists("EventId", int(null)),
                      Users = column_ifexists("Users", ""),
                      UnparsedEvents = column_ifexists("UnparsedEvents", long(null)),
                      UsersTruncated = column_ifexists("UsersTruncated", false)
            | take {{KqlQueryLimits.MaxScheduledTargets + 1}}
            """;
        return Prepare(workspace, wrapped, systems, 1, apiTimeRangeMinutes, "SourceDSA",
            AnalysisQueryVisualizationTypes.Stat, KqlExecutionProfile.Scheduled);
    }

    private static PreparedKqlQuery Prepare(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string kql, IReadOnlyList<string> systems, int binMinutes, int timeRangeMinutes,
        string groupBy, string visualizationType, KqlExecutionProfile profile)
    {
        var selectedSystems = Array.AsReadOnly(systems.ToArray());
        return new(workspace, selectedSystems,
            BuildEffectiveKql(kql, selectedSystems, binMinutes, timeRangeMinutes, groupBy, visualizationType),
            binMinutes, timeRangeMinutes, groupBy, visualizationType, profile);
    }

    internal static string BuildEffectiveKql(
        string kql, IReadOnlyList<string> systems, int binMinutes, int timeRangeMinutes,
        string groupBy, string visualizationType)
    {
        if (binMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(binMinutes));
        if (timeRangeMinutes is < 5 or > 43200)
            throw new ArgumentOutOfRangeException(nameof(timeRangeMinutes));
        AnalysisQuerySelections.Validate(null, groupBy);
        var encodedSystems = JsonSerializer.Serialize(systems);
        var parameters = $"let _SelectedSystems = dynamic({encodedSystems});\nlet _BinSize = {binMinutes}m;\nlet _TimePeriod = {timeRangeMinutes}m;\nlet _GroupBy = \"{groupBy}\";\n";
        return visualizationType == AnalysisQueryVisualizationTypes.Table
            ? $"set truncationmaxsize={KqlQueryLimits.MaxTableResponseBytes};\n{parameters}let _MonitoringTableResult = () {{\n{kql}\n}};\n_MonitoringTableResult()\n| take {KqlQueryLimits.MaxTableRows + 1}"
            : parameters + kql;
    }
}
