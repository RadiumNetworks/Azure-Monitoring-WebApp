using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

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
    public string? PrivateIP { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public double? NtdsFileSizeInGb { get; set; }
    public string CertificatesJson { get; set; } = "[]";
    public string ServicesJson { get; set; } = "[]";
    public bool Maintenance { get; set; }
    public string Computer { get; set; } = string.Empty;
    public ICollection<ParsedAlertRecord> ParsedAlerts { get; set; } = [];

    [NotMapped]
    public IReadOnlyList<InventoryCertificate> Certificates =>
        Deserialize<InventoryCertificate>(CertificatesJson);

    [NotMapped]
    public IReadOnlyList<InventoryService> Services =>
        Deserialize<InventoryService>(ServicesJson);

    [NotMapped]
    public bool HasServiceHealthAlert =>
        Services.Any(service => service.Status != 4);

    private static IReadOnlyList<T> Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<T[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed record InventoryCertificate(
    string Name,
    string SerialNumber,
    DateTimeOffset? ExpiresAt);

public sealed record InventoryService(string Name, int? Status);