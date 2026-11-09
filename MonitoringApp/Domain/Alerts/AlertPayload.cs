using System.Text.Json;

namespace MonitoringApp;

internal static class AlertPayload
{
    public static (AlertRecord Alert, DateTimeOffset? ResolvedAt) Parse(JsonElement payload)
    {
        var data = GetObject(payload, "data");
        var essentials = GetObject(data, "essentials");
        var alertContext = GetObject(data, "alertContext");
        var context = essentials.ValueKind == JsonValueKind.Object ? essentials : payload;
        var targetResource = GetFirstArrayValue(context, "alertTargetIDs")
            ?? GetFirstArrayValue(data, "alertTargetIDs");
        var resourceGroup = GetString(context, "targetResourceGroup", "resourceGroupName");
        var subscriptionId = GetString(context, "targetSubscriptionId", "subscriptionId");

        if (!string.IsNullOrWhiteSpace(targetResource))
        {
            resourceGroup = FirstNonEmpty(resourceGroup, GetResourceIdSegment(targetResource, "resourceGroups"));
            subscriptionId = FirstNonEmpty(
                GetResourceIdSegment(targetResource, "subscriptions"),
                subscriptionId);
        }

        var alert = new AlertRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            GetString(context, "alertId"),
            GetString(context, "alertRule", "name", "alertName"),
            GetString(context, "severity"),
            GetString(context, "alertState", "status"),
            GetString(context, "signalType"),
            GetString(context, "monitorCondition"),
            targetResource ?? GetString(context, "targetResourceName", "resourceId"),
            resourceGroup,
            subscriptionId,
            GetDateTime(context, "firedDateTime", "startDateTime", "timestamp"),
            GetString(context, "description"),
            GetHttpUrl(alertContext, "linkToSearchResultsUI"),
            string.Empty,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        return (alert, GetDateTime(context, "resolvedDateTime"));
    }


    /// <summary>
    /// Walks a property path through nested JSON objects. A missing segment or non-object value returns the default JsonElement.
    /// </summary>
    private static JsonElement GetObject(JsonElement source, params string[] path)
    {
        var current = source;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return default;
            }
        }

        return current;
    }

    /// <summary>
    /// Reads the first available property from a list of alternative names. String values are returned directly and other JSON values are converted to text.
    /// </summary>
    private static string GetString(JsonElement source, params string[] names)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (source.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Recursively finds the first valid HTTP or HTTPS URL stored under the requested property name. Missing and invalid URLs produce an empty string.
    /// </summary>
    private static string GetHttpUrl(JsonElement source, string propertyName)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    {
                        return uri.AbsoluteUri;
                    }
                }

                var nestedValue = GetHttpUrl(property.Value, propertyName);
                if (nestedValue.Length > 0)
                {
                    return nestedValue;
                }
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
            {
                var nestedValue = GetHttpUrl(item, propertyName);
                if (nestedValue.Length > 0)
                {
                    return nestedValue;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the first value from a named JSON array. Missing, non-array, or empty properties return null.
    /// </summary>
    private static string? GetFirstArrayValue(JsonElement source, string name)
    {
        if (source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty(name, out var values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                return value.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts and URL-decodes the value following a named segment in an Azure resource ID. The method returns an empty string when the segment is absent.
    /// </summary>
    private static string GetResourceIdSegment(string resourceId, string segmentName)
    {
        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals(segmentName, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[index + 1]);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the preferred value when it contains text, otherwise the fallback value. This is used to combine explicit payload fields with resource-ID-derived values.
    /// </summary>
    internal static string FirstNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    /// <summary>
    /// Reads the first matching JSON property and parses it as a timestamp. Missing or invalid values return null.
    /// </summary>
    private static DateTimeOffset? GetDateTime(JsonElement source, params string[] names)
    {
        var value = GetString(source, names);
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }
}
