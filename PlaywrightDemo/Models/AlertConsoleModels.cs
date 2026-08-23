namespace PlaywrightDemo.Models;

public sealed record ResultSummary(int Shown, int Available);

public sealed record NavigationNode(string Name, int Count);

public sealed record ChartSummary(int Total, int MaximumPerHour);

public sealed record AlertRow(
    string Received,
    string Alert,
    string TargetName,
    string SearchQuery,
    string SearchResult,
    string Comments,
    bool HasSearchResults);