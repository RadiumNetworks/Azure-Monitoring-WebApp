using System.Globalization;
using System.Text.Json;

namespace MonitoringApp;

public sealed record InventoryHeartbeatObservation(
    string SubscriptionId,
    string Computer,
    string? Domain,
    string? PrivateIP,
    DateTimeOffset LastHeartbeatAt);

public static class InventoryHeartbeatBatch
{
    public const string AlertName = "Inventory Heartbeat Status";
    public const string QueryResultType = "InventoryHeartbeat";

    public static bool IsInventoryHeartbeat(JsonElement payload) =>
        TryGetQueryResult(payload, out var queryResult) &&
        GetString(queryResult, "type").Equals(QueryResultType, StringComparison.OrdinalIgnoreCase);

    public static bool IsInventoryHeartbeat(AlertRecord alert)
    {
        if (alert.Name.Equals(AlertName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(alert.RawJson);
            return IsInventoryHeartbeat(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<InventoryHeartbeatObservation> Parse(JsonElement payload)
    {
        if (!TryGetQueryResult(payload, out var queryResult) ||
            !queryResult.TryGetProperty("columns", out var columns) ||
            columns.ValueKind != JsonValueKind.Array ||
            !queryResult.TryGetProperty("rows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var indexes = columns.EnumerateArray()
            .Select((column, index) => (Name: GetString(column, "name"), Index: index))
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .ToDictionary(column => column.Name, column => column.Index, StringComparer.OrdinalIgnoreCase);
        if (!indexes.TryGetValue("SubscriptionId", out var subscriptionIndex) ||
            !indexes.TryGetValue("Computer", out var computerIndex) ||
            !indexes.TryGetValue("LastHeartbeat", out var heartbeatIndex))
        {
            return [];
        }

        indexes.TryGetValue("Domain", out var domainIndex);
        indexes.TryGetValue("PrivateIP", out var privateIpIndex);
        var observations = rows.EnumerateArray()
            .Where(row => row.ValueKind == JsonValueKind.Array)
            .Select(row => row.EnumerateArray().ToArray())
            .Select(values => CreateObservation(
                values,
                subscriptionIndex,
                computerIndex,
                heartbeatIndex,
                indexes.ContainsKey("Domain") ? domainIndex : null,
                indexes.ContainsKey("PrivateIP") ? privateIpIndex : null))
            .Where(observation => observation is not null)
            .Cast<InventoryHeartbeatObservation>()
            .GroupBy(
                observation => (observation.SubscriptionId, observation.Computer),
                InventoryHeartbeatKeyComparer.Instance)
            .Select(group => group.OrderByDescending(observation => observation.LastHeartbeatAt).First())
            .ToArray();

        return observations;
    }

    private static InventoryHeartbeatObservation? CreateObservation(
        JsonElement[] values,
        int subscriptionIndex,
        int computerIndex,
        int heartbeatIndex,
        int? domainIndex,
        int? privateIpIndex)
    {
        var subscriptionId = GetValue(values, subscriptionIndex).Trim();
        var computer = GetValue(values, computerIndex).Trim();
        if (string.IsNullOrWhiteSpace(subscriptionId) || subscriptionId.Length > 64 ||
            string.IsNullOrWhiteSpace(computer) || computer.Length > 256 ||
            !DateTimeOffset.TryParse(
                GetValue(values, heartbeatIndex),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var lastHeartbeatAt))
        {
            return null;
        }

        return new InventoryHeartbeatObservation(
            subscriptionId,
            computer,
            NormalizeOptional(GetValue(values, domainIndex), 256),
            NormalizeOptional(GetValue(values, privateIpIndex), 256),
            lastHeartbeatAt.ToUniversalTime());
    }

    private static bool TryGetQueryResult(JsonElement payload, out JsonElement queryResult)
    {
        queryResult = default;
        return payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("queryResult", out queryResult) &&
            queryResult.ValueKind == JsonValueKind.Object;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetValue(JsonElement[] values, int? index) =>
        index is >= 0 && index < values.Length
            ? values[index.Value].ValueKind == JsonValueKind.String
                ? values[index.Value].GetString() ?? string.Empty
                : values[index.Value].ToString()
            : string.Empty;

    private static string? NormalizeOptional(string value, int maximumLength)
    {
        var normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength
            ? null
            : normalized;
    }

    private sealed class InventoryHeartbeatKeyComparer : IEqualityComparer<(string SubscriptionId, string Computer)>
    {
        public static InventoryHeartbeatKeyComparer Instance { get; } = new();

        public bool Equals(
            (string SubscriptionId, string Computer) left,
            (string SubscriptionId, string Computer) right) =>
            left.SubscriptionId.Equals(right.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
            left.Computer.Equals(right.Computer, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string SubscriptionId, string Computer) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.SubscriptionId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Computer));
    }
}