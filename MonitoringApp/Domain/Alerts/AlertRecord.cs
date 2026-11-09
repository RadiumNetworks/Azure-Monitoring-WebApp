using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Represents one persisted Azure Monitor alert event together with its original JSON payload. Computed properties derive display names, site information, and the search query from that payload.
/// </summary>
public sealed record AlertRecord(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string AlertId,
    string Name,
    string Severity,
    string Status,
    string SignalType,
    string MonitorCondition,
    string Target,
    string ResourceGroup,
    string SubscriptionId,
    DateTimeOffset? FiredAt,
    string Description,
    string SearchResultsUrl,
    string Comments,
    string RawJson)
{
    public AlertDisplayIdentity? DisplayIdentity { get; init; }
    public DateTimeOffset? QueryResultRefreshedAt { get; init; }
    public string TargetName => DisplayIdentity?.TargetName ?? GetTargetName(GetConfigurationItem() ?? Target);
    public string SiteName => DisplayIdentity?.SiteName ?? string.Empty;
    public string TargetDisplayName => string.IsNullOrWhiteSpace(SiteName)
        ? TargetName
        : $"{TargetName} ({SiteName})";
    public string SearchQuery => GetSearchQuery(RawJson);

    /// <summary>
    /// Finds the first meaningful value for any requested dimension name in the alert criteria. Missing or invalid payload data returns null.
    /// </summary>
    internal string? GetDimensionValue(params string[] names)
    {
        if (string.IsNullOrWhiteSpace(RawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(RawJson);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("alertContext", out var alertContext) ||
                !alertContext.TryGetProperty("condition", out var condition) ||
                !condition.TryGetProperty("allOf", out var allOf) ||
                allOf.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var criterion in allOf.EnumerateArray())
            {
                if (!criterion.TryGetProperty("dimensions", out var dimensions) ||
                    dimensions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var dimension in dimensions.EnumerateArray())
                {
                    if (dimension.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String &&
                        names.Contains(nameElement.GetString(), StringComparer.OrdinalIgnoreCase) &&
                        dimension.TryGetProperty("value", out var valueElement) &&
                        valueElement.ValueKind == JsonValueKind.String &&
                        valueElement.GetString() is { } value &&
                        IsMeaningfulTarget(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the first meaningful configuration item from the Common Alert Schema essentials. Missing or invalid payload data returns null.
    /// </summary>
    private string? GetConfigurationItem()
    {
        if (string.IsNullOrWhiteSpace(RawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(RawJson);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("essentials", out var essentials) ||
                !essentials.TryGetProperty("configurationItems", out var configurationItems) ||
                configurationItems.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return configurationItems
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .FirstOrDefault(item => item is not null && IsMeaningfulTarget(item));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether a target value contains usable content rather than the Azure Monitor empty placeholder. Whitespace-only values are rejected.
    /// </summary>
    internal static bool IsMeaningfulTarget(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("<EMPTY_VALUE>", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the final segment from a slash- or backslash-delimited target path. A plain name is returned unchanged.
    /// </summary>
    internal static string GetTargetName(string value)
    {
        var normalizedValue = value.TrimEnd('/', '\\');
        var separatorIndex = normalizedValue.LastIndexOfAny(['/', '\\']);
        return separatorIndex >= 0 ? normalizedValue[(separatorIndex + 1)..] : normalizedValue;
    }

    /// <summary>
    /// Parses an alert payload and searches it for the first searchQuery property. Empty or invalid JSON returns an empty string.
    /// </summary>
    private static string GetSearchQuery(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return FindSearchQuery(document.RootElement) ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Recursively searches JSON objects and arrays for a string-valued searchQuery property. It returns null when no query is present.
    /// </summary>
    private static string? FindSearchQuery(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("searchQuery", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nestedValue = FindSearchQuery(property.Value);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nestedValue = FindSearchQuery(item);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Reports the alert returned by ingestion and whether a new database row was created or intentionally suppressed.
/// </summary>
public sealed record AddAlertResult(AlertRecord Alert, bool Created, bool Suppressed = false);

/// <summary>
/// Identifies one active alert that can receive a scheduled query-result refresh.
/// </summary>
public sealed record AlertQueryRefreshTarget(
    Guid Id,
    string AlertId,
    string Type,
    string DimensionName,
    string DimensionValue);

/// <summary>
/// Describes the persistence outcome of an alert query-result refresh.
/// </summary>
public enum AlertQueryRefreshUpdateStatus
{
    Updated,
    Unchanged,
    NotFound,
    NotActive,
    TypeMismatch,
    DimensionMismatch,
    Stale
}

/// <summary>
/// Reports the outcome and current refresh time for an alert query-result update.
/// </summary>
public sealed record AlertQueryRefreshUpdateResult(
    AlertQueryRefreshUpdateStatus Status,
    DateTimeOffset? QueryResultRefreshedAt);
