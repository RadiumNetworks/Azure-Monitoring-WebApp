using System.Globalization;
using System.Text.Json;

namespace MonitoringApp;

internal static class ScheduledQueryResultNormalizer
{
    internal static (Dictionary<string, double?> Values, Dictionary<string, ScheduledQueryEventDetails> EventDetails) Normalize(
        AnalysisQueryTable table, string scope, IReadOnlyList<ScheduledQueryTarget> targets)
    {
        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var eventDetails = new Dictionary<string, ScheduledQueryEventDetails>(StringComparer.OrdinalIgnoreCase);
        var seriesIndex = table.Columns.ToList().FindIndex(column => column.Name == "Series");
        var valueIndex = table.Columns.ToList().FindIndex(column => column.Name == "Value");
        if (seriesIndex < 0 || valueIndex < 0 || table.Rows.Any(row => string.IsNullOrWhiteSpace(row[seriesIndex]) ||
            row[valueIndex] is not null && (!double.TryParse(row[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))))
            throw new InvalidOperationException("Scheduled queries must return Series and numeric or null Value columns.");
        var names = targets.Select(item => item.Series).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            var computer = row[seriesIndex]!;
            if (!names.Contains(computer) || !values.TryAdd(computer,
                row[valueIndex] is null ? null : double.Parse(row[valueIndex]!, CultureInfo.InvariantCulture)))
                throw new InvalidOperationException(scope == AnalysisQueryScopes.Workspace
                    ? "Workspace-wide KQL must return at most one Series = Workspace row with a numeric or null Value."
                    : "KQL must return at most one Series/Value row per selected Computer, without unknown or duplicate systems.");
            if (scope == AnalysisQueryScopes.Workspace && ReadEventDetails(table, row) is { } details)
                eventDetails.Add(computer, details);
        }
        return (values, eventDetails);
    }

    internal static (ScheduledQueryTarget[] Targets, Dictionary<string, double?> Values) NormalizeUnknownTargets(
        AnalysisQueryTable table, string workspaceKey, IReadOnlyList<string> inventoryComputers)
    {
        var targetIndex = table.Columns.ToList().FindIndex(column => column.Name == "Target");
        var countIndex = table.Columns.ToList().FindIndex(column => column.Name == "count_");
        if (targetIndex < 0 || countIndex < 0 ||
            table.Columns.Count(column => column.Name == "Target") != 1 || table.Columns.Count(column => column.Name == "count_") != 1 ||
            table.Rows.Any(row => row.Count != table.Columns.Count))
            throw new InvalidOperationException("Unknown inventory target queries must return Target and count_ columns.");
        var known = inventoryComputers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<ScheduledQueryTarget>();
        foreach (var row in table.Rows)
        {
            var target = row[targetIndex]?.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(target) || target.Length > 256 || target.Any(char.IsControl) ||
                !AlertRecord.IsMeaningfulTarget(target) ||
                !long.TryParse(row[countIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0 ||
                count > 9_007_199_254_740_991L ||
                !values.TryAdd(target, count))
                throw new InvalidOperationException("Each unknown Target must be nonempty, unique, at most 256 characters and have a positive integer count_ within the exact numeric range (1-9007199254740991).");
            if (known.Contains(target))
                throw new InvalidOperationException("The query returned a Target that is already in the workspace Inventory baseline.");
            targets.Add(new("", target, WorkspaceKey: workspaceKey) { IsUnknownInventoryTarget = true });
        }
        return (targets.ToArray(), values);
    }

    internal static ScheduledQueryEventDetails? ReadEventDetails(AnalysisQueryTable table, IReadOnlyList<string?> row)
    {
        string? Cell(string name)
        {
            var index = table.Columns.ToList().FindIndex(column => column.Name == name);
            return index < 0 ? null : row[index];
        }
        var id = Cell("EventId");
        if (id is null)
        {
            if (!string.IsNullOrEmpty(Cell("Users")) || !string.IsNullOrEmpty(Cell("UnparsedEvents")) ||
                Cell("UsersTruncated") is { } flag && (!bool.TryParse(flag, out var truncatedFlag) || truncatedFlag))
                throw new InvalidOperationException("Event details require an EventId.");
            return null;
        }
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId) || eventId is < 1 or > 65535 ||
            !long.TryParse(Cell("UnparsedEvents"), NumberStyles.None, CultureInfo.InvariantCulture, out var unparsed) || unparsed < 0 ||
            !bool.TryParse(Cell("UsersTruncated"), out var truncated))
            throw new InvalidOperationException("Event details require a valid EventId, nonnegative UnparsedEvents and boolean UsersTruncated.");
        string[] users;
        try
        {
            users = JsonSerializer.Deserialize<string[]>(Cell("Users") ?? "")
                ?? throw new InvalidOperationException("Users must be a JSON array of account names.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Users must be a JSON array of account names.", exception);
        }
        if (users.Length > ScheduledQueryEventDetails.MaxUsers || users.Any(user => string.IsNullOrWhiteSpace(user) || user.Length > 1024))
            throw new InvalidOperationException($"Event details accept at most {ScheduledQueryEventDetails.MaxUsers} nonempty account entries of at most 1024 characters each.");
        return new(eventId, users, unparsed, truncated);
    }
}
