using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Extracts the normalized alert fields persisted in ParsedAlerts.
/// </summary>
public static class ParsedAlertFactory
{
    public static ParsedAlertRecord Create(AlertRecord alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var inventorySubscriptionId = NormalizeKey(alert.SubscriptionId, 64);
        var inventoryComputer = NormalizeKey(alert.TargetName, 256);
        if (inventorySubscriptionId is null || inventoryComputer is null)
        {
            inventorySubscriptionId = null;
            inventoryComputer = null;
        }

        return new ParsedAlertRecord
        {
            Id = alert.Id,
            FiredDateTime = alert.FiredAt,
            AlertId = alert.AlertId,
            OriginalAlertId = ExtractOriginalAlertId(alert.RawJson, alert.AlertId),
            Severity = alert.Severity,
            MonitorCondition = alert.MonitorCondition,
            Dimensions = ExtractDimensions(alert.RawJson),
            SearchQuery = alert.SearchQuery,
            QueryResults = ExtractRootProperty(alert.RawJson, "queryResult", "{}"),
            AlertName = alert.Name,
            ResourceGroup = alert.ResourceGroup,
            InventorySubscriptionId = inventorySubscriptionId,
            InventoryComputer = inventoryComputer
        };
    }

    private static string? NormalizeKey(string value, int maximumLength)
    {
        var normalized = value.Trim();
        return normalized.Length is > 0 && normalized.Length <= maximumLength ? normalized : null;
    }

    private static string ExtractOriginalAlertId(string rawJson, string fallback)
    {
        if (!TryParse(rawJson, out var document))
        {
            return fallback;
        }

        using (document)
        {
            var value = FindStringProperty(
                document.RootElement,
                "originalAlertId",
                "originAlertId",
                "originalId");
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    private static string ExtractDimensions(string rawJson)
    {
        if (!TryParse(rawJson, out var document))
        {
            return "[]";
        }

        using (document)
        {
            var dimensions = new List<JsonElement>();
            CollectProperties(document.RootElement, "dimensions", dimensions);
            var flattened = dimensions
                .Where(element => element.ValueKind == JsonValueKind.Array)
                .SelectMany(element => element.EnumerateArray())
                .Select(element => element.Clone())
                .ToArray();
            return JsonSerializer.Serialize(flattened);
        }
    }

    private static string ExtractRootProperty(string rawJson, string propertyName, string fallback)
    {
        if (!TryParse(rawJson, out var document))
        {
            return fallback;
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.EnumerateObject().FirstOrDefault(property =>
                    property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)) is var match &&
                match.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                return match.Value.GetRawText();
            }

            return fallback;
        }
    }

    private static void CollectProperties(JsonElement element, string propertyName, List<JsonElement> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(property.Value.Clone());
                }
                else
                {
                    CollectProperties(property.Value, propertyName, values);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectProperties(item, propertyName, values);
            }
        }
    }

    private static string? FindStringProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nested = FindStringProperty(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringProperty(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryParse(string rawJson, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(rawJson);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
