using System.Globalization;
using System.Text.Json;

namespace MonitoringApp;

public sealed record InventoryHeartbeatObservation(
    string SubscriptionId,
    string Computer,
    string? Domain,
    string? Site,
    string? PrivateIP,
    DateTimeOffset LastHeartbeatAt,
    double? NtdsFileSizeInGb,
    string? CertificatesJson,
    string? ServicesJson,
    bool HasNtdsFileSizeData,
    bool IsDomainController);

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
        indexes.TryGetValue("Site", out var siteIndex);
        indexes.TryGetValue("PrivateIP", out var privateIpIndex);
        indexes.TryGetValue("NTDSFileSizeinGB", out var ntdsFileSizeIndex);
        indexes.TryGetValue("Certificates", out var certificatesIndex);
        indexes.TryGetValue("LDAPSCertificate", out var ldapsCertificateIndex);
        indexes.TryGetValue("Services", out var servicesIndex);
        var observations = rows.EnumerateArray()
            .Where(row => row.ValueKind == JsonValueKind.Array)
            .Select(row => row.EnumerateArray().ToArray())
            .Select(values => CreateObservation(
                values,
                subscriptionIndex,
                computerIndex,
                heartbeatIndex,
                indexes.ContainsKey("Domain") ? domainIndex : null,
                indexes.ContainsKey("Site") ? siteIndex : null,
                indexes.ContainsKey("PrivateIP") ? privateIpIndex : null,
                indexes.ContainsKey("NTDSFileSizeinGB") ? ntdsFileSizeIndex : null,
                indexes.ContainsKey("Certificates") ? certificatesIndex : null,
                indexes.ContainsKey("LDAPSCertificate") ? ldapsCertificateIndex : null,
                indexes.ContainsKey("Services") ? servicesIndex : null))
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
        int? siteIndex,
        int? privateIpIndex,
        int? ntdsFileSizeIndex,
        int? certificatesIndex,
        int? ldapsCertificateIndex,
        int? servicesIndex)
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

        var hasCertificateData = certificatesIndex.HasValue || ldapsCertificateIndex.HasValue;
        var certificates = ParseCertificates(
            GetElement(values, certificatesIndex),
            GetElement(values, ldapsCertificateIndex));
        var services = ParseServices(GetElement(values, servicesIndex));
        var ntdsFileSizeInGb = ParseOptionalDouble(GetValue(values, ntdsFileSizeIndex));
        return new InventoryHeartbeatObservation(
            subscriptionId,
            computer,
            NormalizeOptional(GetValue(values, domainIndex), 256),
            NormalizeOptional(GetValue(values, siteIndex), 256),
            NormalizeOptional(GetValue(values, privateIpIndex), 256),
            lastHeartbeatAt.ToUniversalTime(),
            ntdsFileSizeInGb,
            hasCertificateData ? JsonSerializer.Serialize(certificates) : null,
            servicesIndex.HasValue ? JsonSerializer.Serialize(services) : null,
            ntdsFileSizeIndex.HasValue,
            ntdsFileSizeInGb.HasValue ||
                services.Any(service => service.Name.Equals("NTDS", StringComparison.OrdinalIgnoreCase)));
    }

    private static InventoryCertificate[] ParseCertificates(params JsonElement?[] values) =>
        values
            .SelectMany(ParseObjects)
            .Select(element => new InventoryCertificate(
                FirstString(element, "Name", "Subject"),
                FirstString(element, "SerialNumber", "Thumbprint"),
                ParseOptionalDateTime(FirstString(element, "ExpirationDate", "ExpiresAt", "NotAfter"))))
            .Where(certificate =>
                !string.IsNullOrWhiteSpace(certificate.Name) ||
                !string.IsNullOrWhiteSpace(certificate.SerialNumber))
            .DistinctBy(
                certificate => $"{certificate.Name}\u001f{certificate.SerialNumber}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static InventoryService[] ParseServices(JsonElement? value) =>
        ParseObjects(value)
            .Select(element => new InventoryService(
                FirstString(element, "Name"),
                TryGetInt32(element, "Status")))
            .Where(service => !string.IsNullOrWhiteSpace(service.Name))
            .DistinctBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<JsonElement> ParseObjects(JsonElement? value)
    {
        if (value is not { } element ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var json = element.GetString();
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                return ParseObjects(document.RootElement).Select(item => item.Clone()).ToArray();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => [element],
            JsonValueKind.Array => element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .ToArray(),
            _ => []
        };
    }

    private static string FirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }

        return string.Empty;
    }

    private static int? TryGetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static double? ParseOptionalDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static DateTimeOffset? ParseOptionalDateTime(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var result)
            ? result.ToUniversalTime()
            : null;

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

    private static JsonElement? GetElement(JsonElement[] values, int? index) =>
        index is >= 0 && index < values.Length ? values[index.Value] : null;

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