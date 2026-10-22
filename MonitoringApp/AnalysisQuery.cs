namespace MonitoringApp;

public sealed class AnalysisQueryDefinition
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Kql { get; set; } = string.Empty;
    public string Scope { get; set; } = AnalysisQueryScopes.SelectedSystems;
    public string VisualizationType { get; set; } = AnalysisQueryVisualizationTypes.LineChart;
    public string VisualizationJson { get; set; } = "{}";
    public string Unit { get; set; } = string.Empty;
    public int DefaultTimeRangeMinutes { get; set; } = 1440;
    public int MaxTimeRangeMinutes { get; set; } = 10080;
    public int DefaultBinMinutes { get; set; } = 5;
    public string MinimumRole { get; set; } = SqlAuthenticationRoles.Operator;
    public string Status { get; set; } = AnalysisQueryStatuses.Draft;
    public bool Enabled { get; set; } = true;
    public int Version { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public ICollection<AnalysisQueryWorkspace> Workspaces { get; set; } = [];
    public ICollection<AnalysisQueryVersion> Versions { get; set; } = [];
}

public sealed class AnalysisQueryWorkspace
{
    public Guid QueryDefinitionId { get; set; }
    public string WorkspaceKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastValidatedAt { get; set; }
    public string LastValidationStatus { get; set; } = AnalysisQueryValidationStatuses.NotValidated;
    public string LastValidationMessage { get; set; } = string.Empty;
    public AnalysisQueryDefinition QueryDefinition { get; set; } = null!;
}

public sealed class AnalysisQueryVersion
{
    public Guid Id { get; set; }
    public Guid QueryDefinitionId { get; set; }
    public int Version { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; }
    public string PublishedBy { get; set; } = string.Empty;
    public AnalysisQueryDefinition QueryDefinition { get; set; } = null!;
}

public static class AnalysisQueryStatuses
{
    public const string Draft = "Draft";
    public const string Validated = "Validated";
    public const string Published = "Published";
    public const string Disabled = "Disabled";
}

public static class AnalysisQueryValidationStatuses
{
    public const string NotValidated = "NotValidated";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

public static class AnalysisQueryVisualizationTypes
{
    public const string LineChart = "LineChart";
    public const string Table = "Table";
    public const string Honeycomb = "Honeycomb";
    public const string BarChart = "BarChart";
    public const string BarGauge = "BarGauge";
    public const string Stat = "Stat";
    public const string StatusHistory = "StatusHistory";
    public static IReadOnlyList<string> All { get; } = [LineChart, Table, Honeycomb, BarChart, BarGauge, Stat, StatusHistory];
}

public static class AnalysisQueryScopes
{
    public const string SelectedSystems = "SelectedSystems";
    public const string Workspace = "Workspace";
    public static IReadOnlyList<string> All { get; } = [SelectedSystems, Workspace];
}

public static class AnalysisQuerySelections
{
    public static IReadOnlyList<int> TimeRanges { get; } = [60, 240, 480, 1440, 2880, 10080, 43200];
    public static IReadOnlyList<int> BinSizes { get; } = [15, 30, 60];
    public static IReadOnlyDictionary<string, string> Groupings { get; } = new Dictionary<string, string>
    {
        ["NC"] = "Naming Context",
        ["SourceDSA"] = "Source DC",
        ["DestDSA"] = "Destination DC",
        ["SourceDSASite"] = "Source site",
        ["DestDSASite"] = "Destination site"
    };

    public static string TimeRangeLabel(int minutes) => minutes switch
    {
        60 => "1h", 240 => "4h", 480 => "8h", 1440 => "24h",
        2880 => "2 days", 10080 => "7 days", 43200 => "30 days",
        _ => $"{minutes} min"
    };

    public static void Validate(int? binMinutes, string groupBy)
    {
        if (binMinutes.HasValue && !BinSizes.Contains(binMinutes.Value))
            throw new InvalidOperationException("Select an averaging interval of 15 min, 30 min, or 1h.");
        if (groupBy is null || !Groupings.ContainsKey(groupBy))
            throw new InvalidOperationException("Select a supported replication grouping.");
    }
}

public sealed record AnalysisQuerySnapshot(
    string Key,
    string Name,
    string Description,
    string Category,
    string Kql,
    string VisualizationType,
    string Unit,
    int DefaultTimeRangeMinutes,
    int MaxTimeRangeMinutes,
    int DefaultBinMinutes,
    string MinimumRole,
    AnalysisVisualizationOptions? Visualization = null,
    string Scope = AnalysisQueryScopes.SelectedSystems);

public sealed record AnalysisQuerySystem(string SubscriptionId, string Computer);

public sealed record AnalysisQueryRequest(
    Guid QueryDefinitionId,
    IReadOnlyList<AnalysisQuerySystem> Systems,
    int TimeRangeMinutes,
    int? BinMinutes = null,
    string GroupBy = "SourceDSA");

public sealed record AnalysisQueryPoint(
    DateTimeOffset Timestamp,
    double Value,
    string? Label);

public sealed record AnalysisQuerySeries(
    string Name,
    string? Dimension,
    string Workspace,
    IReadOnlyList<AnalysisQueryPoint> Points);

public sealed record AnalysisQueryWarning(string Workspace, string Message);

public sealed record AnalysisQueryExecutionPreview(
    string WorkspaceKey,
    string WorkspaceDisplayName,
    IReadOnlyList<string> Systems,
    string EffectiveKql);

public sealed record AnalysisQueryResponse(
    Guid QueryDefinitionId,
    string Name,
    int Version,
    string Unit,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset ExecutedAt,
    long DurationMilliseconds,
    IReadOnlyList<AnalysisQuerySeries> Series,
    IReadOnlyList<AnalysisQueryWarning> Warnings,
    string VisualizationType = AnalysisQueryVisualizationTypes.LineChart,
    IReadOnlyList<AnalysisQueryTable>? Tables = null,
    AnalysisVisualizationOptions? Visualization = null,
    int? BinMinutes = null,
    string Scope = AnalysisQueryScopes.SelectedSystems);

public sealed record AnalysisQueryColumn(string Name, string Type);

public sealed record AnalysisQueryTable(
    string Workspace,
    IReadOnlyList<AnalysisQueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows);
