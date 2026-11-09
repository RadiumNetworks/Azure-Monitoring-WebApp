namespace MonitoringApp;

public sealed record PublishedAnalysisDashboard(
    Guid Id, string Name, int Version, string Owner, DateTimeOffset PublishedAt);

public sealed record PublishedAnalysisDashboardSnapshot(
    PublishedAnalysisDashboard Publication, AnalysisDashboardDocument Document);
