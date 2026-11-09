using System.Collections.ObjectModel;

namespace MonitoringApp;

internal enum KqlExecutionProfile
{
    Interactive,
    Scheduled
}

internal sealed class PreparedKqlQuery(
    CrossTenantLogAnalyticsWorkspaceOptions workspace,
    ReadOnlyCollection<string> systems,
    string effectiveKql,
    int binMinutes,
    int apiTimeRangeMinutes,
    string groupBy,
    string visualizationType,
    KqlExecutionProfile profile)
{
    internal CrossTenantLogAnalyticsWorkspaceOptions Workspace { get; } = workspace;
    internal IReadOnlyList<string> Systems { get; } = systems;
    internal string EffectiveKql { get; } = effectiveKql;
    internal int BinMinutes { get; } = binMinutes;
    internal int ApiTimeRangeMinutes { get; } = apiTimeRangeMinutes;
    internal string GroupBy { get; } = groupBy;
    internal string VisualizationType { get; } = visualizationType;
    internal KqlExecutionProfile Profile { get; } = profile;

    internal AnalysisQueryExecutionPreview CreatePreview(bool sortSystems = false) =>
        new(Workspace.Key, Workspace.DisplayName,
            sortSystems ? Array.AsReadOnly(Systems.Order(StringComparer.OrdinalIgnoreCase).ToArray()) : Systems,
            EffectiveKql);
}
