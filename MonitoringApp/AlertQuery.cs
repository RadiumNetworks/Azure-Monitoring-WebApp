namespace MonitoringApp;

public static class AlertQuery
{
    public static IReadOnlyList<AlertRecord> GetActiveSince(
        IEnumerable<AlertRecord> alerts,
        DateTimeOffset since) => AlertLifecycle
        .GetActiveAlerts(alerts)
        .Where(alert => alert.ReceivedAt >= since)
        .ToArray();

    public static IReadOnlyList<AlertRecord> GetEventsSince(
        IEnumerable<AlertRecord> alerts,
        DateTimeOffset since) => alerts
        .Where(alert => alert.ReceivedAt >= since)
        .OrderByDescending(alert => alert.ReceivedAt)
        .ToArray();
}

public sealed record AlertQueryItem(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string AlertId,
    string AlertName,
    string Subscription,
    string ResourceGroup,
    string Target,
    string Severity,
    string Status,
    string MonitorCondition,
    DateTimeOffset? FiredAt,
    string Description,
    string? SearchResultLink,
    string Comments)
{
    public static AlertQueryItem FromAlert(AlertRecord alert) => new(
        alert.Id,
        alert.ReceivedAt,
        alert.AlertId,
        alert.Name,
        alert.SubscriptionId,
        alert.ResourceGroup,
        alert.TargetName,
        alert.Severity,
        alert.Status,
        alert.MonitorCondition,
        alert.FiredAt,
        alert.Description,
        string.IsNullOrWhiteSpace(alert.SearchResultsUrl) ? null : alert.SearchResultsUrl,
        alert.Comments);
}

public sealed record AlertQueryResponse(
    string Mode,
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    IReadOnlyList<AlertQueryItem> Alerts);