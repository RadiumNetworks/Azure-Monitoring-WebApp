using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Extracts inventory facts that are explicitly reported by alert query results.
/// </summary>
public static class AlertInventoryLearning
{
    /// <summary>
    /// Reads local and remote computer/domain pairs from DC Port query-result rows.
    /// Invalid, incomplete, and conflicting values are ignored.
    /// </summary>
    public static IReadOnlyList<LearnedComputerDomain> LearnComputerDomains(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("queryResult", out var queryResult) ||
                !TryGetString(queryResult, "type", out var type) ||
                !type.Equals("DCPort", StringComparison.OrdinalIgnoreCase) ||
                !queryResult.TryGetProperty("columns", out var columns) ||
                columns.ValueKind != JsonValueKind.Array ||
                !queryResult.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var columnIndexes = columns.EnumerateArray()
                .Select((column, index) => (Name: GetString(column, "name"), Index: index))
                .Where(column => !string.IsNullOrWhiteSpace(column.Name))
                .ToDictionary(column => column.Name, column => column.Index, StringComparer.OrdinalIgnoreCase);
            var learnedDomains = new Dictionary<string, LearnedComputerDomain>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Array))
            {
                var values = row.EnumerateArray().ToArray();
                LearnPair(values, columnIndexes, "LocalSystem", "LocalDomain", learnedDomains, conflicts);
                LearnPair(values, columnIndexes, "RemoteSystem", "RemoteDomain", learnedDomains, conflicts);
            }

            return learnedDomains
                .Where(pair => !conflicts.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void LearnPair(
        JsonElement[] values,
        IReadOnlyDictionary<string, int> columnIndexes,
        string systemColumn,
        string domainColumn,
        IDictionary<string, LearnedComputerDomain> learnedDomains,
        ISet<string> conflicts)
    {
        var system = GetRowString(values, columnIndexes, systemColumn);
        var domain = GetRowString(values, columnIndexes, domainColumn);
        if (!AlertRecord.IsMeaningfulTarget(system) || !AlertRecord.IsMeaningfulTarget(domain))
        {
            return;
        }

        var computer = AlertRecord.GetTargetName(system).Trim();
        domain = domain.Trim();
        if (computer.Length is 0 or > 256 || domain.Length > 256)
        {
            return;
        }

        if (learnedDomains.TryGetValue(computer, out var existing) &&
            !existing.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
        {
            conflicts.Add(computer);
            return;
        }

        learnedDomains[computer] = new LearnedComputerDomain(computer, domain);
    }

    private static string GetRowString(
        JsonElement[] values,
        IReadOnlyDictionary<string, int> columnIndexes,
        string columnName) =>
        columnIndexes.TryGetValue(columnName, out var index) &&
        index < values.Length &&
        values[index].ValueKind == JsonValueKind.String
            ? values[index].GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetString(JsonElement source, string propertyName, out string value)
    {
        value = GetString(source, propertyName);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetString(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

public sealed record LearnedComputerDomain(string Computer, string Domain);