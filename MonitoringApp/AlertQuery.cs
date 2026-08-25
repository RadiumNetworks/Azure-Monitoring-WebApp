namespace MonitoringApp;

/// <summary>
/// Provides reusable time-window queries over alert records. It supports current active alerts and complete event history.
/// </summary>
public static class AlertQuery
{
    /// <summary>
    /// Returns active alerts received on or after the given timestamp. Lifecycle resolution is applied before the time filter.
    /// </summary>
    public static IReadOnlyList<AlertRecord> GetActiveSince(
        IEnumerable<AlertRecord> alerts,
        DateTimeOffset since) => AlertLifecycle
        .GetActiveAlerts(alerts)
        .Where(alert => alert.ReceivedAt >= since)
        .ToArray();

    /// <summary>
    /// Returns every alert event received on or after the given timestamp. Results are ordered with the newest event first.
    /// </summary>
    public static IReadOnlyList<AlertRecord> GetEventsSince(
        IEnumerable<AlertRecord> alerts,
        DateTimeOffset since) => alerts
        .Where(alert => alert.ReceivedAt >= since)
        .OrderByDescending(alert => alert.ReceivedAt)
        .ToArray();
}

/// <summary>
/// Represents the API-safe projection of one stored alert. It contains display-ready fields without exposing the complete raw payload.
/// </summary>
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
    /// <summary>
    /// Creates an API query item from a stored alert record. Empty search-result URLs are converted to null for cleaner JSON output.
    /// </summary>
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

/// <summary>
/// Wraps an alert-query result with its mode, time range, count, and projected alerts. This is the response shape returned by the query API.
/// </summary>
public sealed record AlertQueryResponse(
    string Mode,
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    IReadOnlyList<AlertQueryItem> Alerts);