namespace MonitoringApp;

/// <summary>
/// Stores query-friendly values extracted from an ingested alert and links them to its inventory computer.
/// </summary>
public sealed class ParsedAlertRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset? FiredDateTime { get; set; }
    public string AlertId { get; set; } = string.Empty;
    public string OriginalAlertId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string MonitorCondition { get; set; } = string.Empty;
    public string Dimensions { get; set; } = "[]";
    public string SearchQuery { get; set; } = string.Empty;
    public string QueryResults { get; set; } = "{}";
    public string AlertName { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;

    public string? InventorySubscriptionId { get; set; }
    public string? InventoryComputer { get; set; }
    public ComputerInventoryEntry? InventoryComputerEntry { get; set; }
}
