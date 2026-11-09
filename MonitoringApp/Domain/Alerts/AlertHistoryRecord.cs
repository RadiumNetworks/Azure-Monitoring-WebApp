namespace MonitoringApp;

/// <summary>
/// Preserves the original state of an alert when it is first received as fired.
/// </summary>
public sealed class AlertHistoryRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string AlertId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SignalType { get; set; } = string.Empty;
    public string MonitorCondition { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public DateTimeOffset? FiredAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string SearchResultsUrl { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string RawJson { get; set; } = string.Empty;

    public static AlertHistoryRecord Create(AlertRecord alert) => new()
    {
        Id = alert.Id,
        ReceivedAt = alert.ReceivedAt,
        AlertId = alert.AlertId,
        Name = alert.Name,
        Severity = alert.Severity,
        Status = alert.Status,
        SignalType = alert.SignalType,
        MonitorCondition = alert.MonitorCondition,
        Target = alert.Target,
        ResourceGroup = alert.ResourceGroup,
        SubscriptionId = alert.SubscriptionId,
        FiredAt = alert.FiredAt,
        Description = alert.Description,
        SearchResultsUrl = alert.SearchResultsUrl,
        Comments = alert.Comments,
        RawJson = alert.RawJson
    };

    public AlertRecord ToAlertRecord() => new(
        Id,
        ReceivedAt,
        AlertId,
        Name,
        Severity,
        Status,
        SignalType,
        MonitorCondition,
        Target,
        ResourceGroup,
        SubscriptionId,
        FiredAt,
        Description,
        SearchResultsUrl,
        Comments,
        RawJson);
}
