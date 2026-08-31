namespace MonitoringApp;

/// <summary>
/// Associates a computer with its subscription, domain, and site in the monitored inventory.
/// </summary>
public sealed class ComputerInventoryEntry
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? Site { get; set; }
    public string? ResourceGroup { get; set; }
    public string? Role { get; set; }
    public string Computer { get; set; } = string.Empty;
    public ICollection<ParsedAlertRecord> ParsedAlerts { get; set; } = [];
}