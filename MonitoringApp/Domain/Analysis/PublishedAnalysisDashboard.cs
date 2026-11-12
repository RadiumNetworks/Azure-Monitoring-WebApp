namespace MonitoringApp;

public sealed record PublishedAnalysisDashboard(
    Guid Id, string Name, int Version, string Owner, DateTimeOffset PublishedAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? OwnerDisplayName { get; init; }
}

public sealed record PublishedAnalysisDashboardSnapshot(
    PublishedAnalysisDashboard Publication, AnalysisDashboardDocument Document);
