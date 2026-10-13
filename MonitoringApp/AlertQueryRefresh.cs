using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonitoringApp;

/// <summary>Describes one supported scheduled alert-query refresh type.</summary>
public sealed record AlertQueryRefreshDefinition(
    string Type,
    string DimensionName,
    string ContextProperty);

/// <summary>Represents an active alert that requires a scheduled query refresh.</summary>
public sealed record AlertQueryRefreshTargetResponse(
    Guid Id,
    string AlertId,
    string Type,
    string DimensionName,
    string DimensionValue);

/// <summary>Contains one refreshed Log Analytics query result.</summary>
public sealed record UpdateAlertQueryResultRequest
{
    public required string Type { get; init; }
    public required string DimensionName { get; init; }
    public required string DimensionValue { get; init; }
    public required JsonElement Columns { get; init; }
    public required JsonElement Rows { get; init; }
    public required int RowCount { get; init; }
    public required DateTimeOffset QueryExecutedAt { get; init; }
}

/// <summary>Reports the persisted state of an alert query-result refresh.</summary>
public sealed record UpdateAlertQueryResultResponse(
    Guid Id,
    bool Updated,
    DateTimeOffset QueryResultRefreshedAt);

/// <summary>
/// Validates and normalizes scheduled query refresh payloads shared by the API and persistence layer.
/// </summary>
public static class AlertQueryRefresh
{
    public const int MaximumColumns = 128;
    public const int MaximumRows = 10_000;
    public const int MaximumSerializedResultLength = 1_000_000;

    private static readonly IReadOnlyDictionary<string, AlertQueryRefreshDefinition> Definitions =
        new[]
        {
            new AlertQueryRefreshDefinition("DCDiag", "Computer", "computer"),
            new AlertQueryRefreshDefinition("Replication", "Computer", "computer"),
            new AlertQueryRefreshDefinition("DCPort", "Computer", "computer"),
            new AlertQueryRefreshDefinition("Heartbeat", "Computer", "computer")
        }.ToDictionary(definition => definition.Type, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> SupportedTypes =>
        Definitions.Values.Select(definition => definition.Type).ToArray();

    public static bool TryGetDefinition(
        string? type,
        out AlertQueryRefreshDefinition definition) =>
        Definitions.TryGetValue(type?.Trim() ?? string.Empty, out definition!);

    public static IReadOnlyDictionary<string, string[]> Validate(
        UpdateAlertQueryResultRequest request,
        DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!TryGetDefinition(request.Type, out var definition))
        {
            errors[nameof(request.Type)] =
                [$"Type must be one of: {string.Join(", ", SupportedTypes)}."];
        }
        else if (!string.Equals(
            request.DimensionName,
            definition.DimensionName,
            StringComparison.OrdinalIgnoreCase))
        {
            errors[nameof(request.DimensionName)] =
                [$"DimensionName must be {definition.DimensionName} for {definition.Type}."];
        }

        if (!IsSafeDimensionValue(request.DimensionValue))
        {
            errors[nameof(request.DimensionValue)] =
                ["DimensionValue is required, may contain at most 256 characters, and contains unsupported characters."];
        }

        if (request.Columns.ValueKind != JsonValueKind.Array)
        {
            errors[nameof(request.Columns)] = ["Columns must be a JSON array."];
        }
        else
        {
            var columns = request.Columns.EnumerateArray().ToArray();
            if (columns.Length > MaximumColumns ||
                columns.Any(column =>
                    column.ValueKind != JsonValueKind.Object ||
                    !column.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(name.GetString()) ||
                    name.GetString()!.Length > 256))
            {
                errors[nameof(request.Columns)] =
                    [$"Columns must contain at most {MaximumColumns} named column objects."];
            }
        }

        if (request.Rows.ValueKind != JsonValueKind.Array)
        {
            errors[nameof(request.Rows)] = ["Rows must be a JSON array."];
        }
        else
        {
            var rows = request.Rows.EnumerateArray().ToArray();
            if (rows.Length > MaximumRows || rows.Any(row => row.ValueKind != JsonValueKind.Array))
            {
                errors[nameof(request.Rows)] =
                    [$"Rows must contain at most {MaximumRows} JSON arrays."];
            }
            else if (request.RowCount != rows.Length)
            {
                errors[nameof(request.RowCount)] = ["RowCount must equal the number of rows."];
            }
        }

        if (request.QueryExecutedAt == default ||
            request.QueryExecutedAt > now.AddMinutes(5))
        {
            errors[nameof(request.QueryExecutedAt)] =
                ["QueryExecutedAt is required and cannot be more than five minutes in the future."];
        }

        if (request.Columns.ValueKind == JsonValueKind.Array &&
            request.Rows.ValueKind == JsonValueKind.Array &&
            request.Columns.GetRawText().Length + request.Rows.GetRawText().Length >
                MaximumSerializedResultLength)
        {
            errors["queryResult"] =
                [$"The serialized query result may contain at most {MaximumSerializedResultLength} characters."];
        }

        return errors;
    }

    public static bool TryReadContext(
        string queryResults,
        out AlertQueryRefreshDefinition definition,
        out string dimensionValue)
    {
        definition = null!;
        dimensionValue = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(queryResults);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !TryGetDefinition(typeElement.GetString(), out definition))
            {
                return false;
            }

            if (document.RootElement.TryGetProperty(definition.ContextProperty, out var dimensionElement) &&
                dimensionElement.ValueKind == JsonValueKind.String)
            {
                dimensionValue = dimensionElement.GetString()?.Trim() ?? string.Empty;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string CreateQueryResultJson(UpdateAlertQueryResultRequest request)
    {
        if (!TryGetDefinition(request.Type, out var definition))
        {
            throw new ArgumentException("The query-result type is not supported.", nameof(request));
        }

        var queryResult = new JsonObject
        {
            ["type"] = definition.Type,
            [definition.ContextProperty] = request.DimensionValue.Trim(),
            ["columns"] = JsonNode.Parse(request.Columns.GetRawText()),
            ["rows"] = JsonNode.Parse(request.Rows.GetRawText()),
            ["rowCount"] = request.RowCount,
            ["refreshedAt"] = request.QueryExecutedAt
        };
        return queryResult.ToJsonString();
    }

    public static string ReplaceQueryResult(string rawJson, string queryResultJson)
    {
        var root = JsonNode.Parse(rawJson) as JsonObject
            ?? throw new JsonException("The stored alert payload must be a JSON object.");
        root["queryResult"] = JsonNode.Parse(queryResultJson);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool IsSafeDimensionValue(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= 256 &&
        !value.Equals("<EMPTY_VALUE>", StringComparison.OrdinalIgnoreCase) &&
        value.IndexOfAny(['"', '\\', '\r', '\n']) < 0;
}
