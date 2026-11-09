using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace MonitoringApp;

internal static class QueryResultPayload
{
    public static bool TryGetQueryResult(JsonElement payload, out JsonElement queryResult) =>
        payload.TryGetProperty("queryResult", out queryResult);

    public static bool TryGetString(JsonElement source, string propertyName, out string value)
    {
        value = GetString(source, propertyName);
        return !string.IsNullOrWhiteSpace(value);
    }

    public static bool TryReadTable(JsonElement queryResult, [NotNullWhen(true)] out QueryResultTable? table)
    {
        table = null;
        if (!queryResult.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array ||
            !queryResult.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var columnIndexes = columns.EnumerateArray()
            .Select((column, index) => (Name: GetString(column, "name"), Index: index))
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .ToDictionary(column => column.Name, column => column.Index, StringComparer.OrdinalIgnoreCase);
        table = new QueryResultTable(
            columnIndexes,
            rows.EnumerateArray()
                .Where(row => row.ValueKind == JsonValueKind.Array)
                .Select(row => row.EnumerateArray().ToArray())
                .ToArray());
        return true;
    }

    private static string GetString(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

// Values borrow the caller's JsonDocument and must be consumed before it is disposed.
internal sealed record QueryResultTable(
    IReadOnlyDictionary<string, int> ColumnIndexes,
    IReadOnlyList<JsonElement[]> Rows);
