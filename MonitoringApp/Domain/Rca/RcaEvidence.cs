using System.Text.Json;

namespace MonitoringApp;

internal static class RcaEvidence
{
    internal static Guid[] Read(Guid rcaId, string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json)
                ?? throw new RcaEvidenceException(rcaId, $"RCA {rcaId} has invalid evidence event IDs.");
        }
        catch (JsonException exception) { throw new RcaEvidenceJsonException(rcaId, exception); }
    }

    internal static string LifecycleKey(AlertRecord alert) =>
        string.IsNullOrWhiteSpace(alert.AlertId) ? $"event:{alert.Id}" : $"alert:{alert.AlertId.ToUpperInvariant()}";
}
