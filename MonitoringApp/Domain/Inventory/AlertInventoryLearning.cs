using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Extracts inventory facts that are explicitly reported by alert query results.
/// </summary>
public static class AlertInventoryLearning
{
    public static bool HasZeroRowCount(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("queryResult", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("rowCount", out var count))
            return false;
        return count.ValueKind == JsonValueKind.Number && count.TryGetDecimal(out var number) && number == 0 ||
            count.ValueKind == JsonValueKind.String && count.GetString()?.Trim() == "0";
    }

    public static bool HasZeroRowCount(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return HasZeroRowCount(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsDcPort(string rawJson) =>
        InventoryRoleRuleMatcher.ReadQueryResultType(rawJson).Equals("DCPort", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads local and remote computer/domain pairs from DC Port query-result rows.
    /// Invalid, incomplete, and conflicting values are ignored.
    /// </summary>
    public static IReadOnlyList<LearnedComputerDomain> LearnComputerDomains(string rawJson) =>
        LearnSystems(rawJson, requireDomain: true);

    private static IReadOnlyList<LearnedComputerDomain> LearnSystems(string rawJson, bool requireDomain)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || HasZeroRowCount(rawJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!QueryResultPayload.TryGetQueryResult(document.RootElement, out var queryResult) ||
                !QueryResultPayload.TryGetString(queryResult, "type", out var type) ||
                !type.Equals("DCPort", StringComparison.OrdinalIgnoreCase) ||
                !QueryResultPayload.TryReadTable(queryResult, out var table))
            {
                return [];
            }

            var learnedDomains = new Dictionary<string, LearnedComputerDomain>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var values in table.Rows)
            {
                LearnPair(values, table.ColumnIndexes, "LocalSystem", "LocalDomain", "LocalSite", learnedDomains, conflicts, requireDomain);
                LearnPair(values, table.ColumnIndexes, "RemoteSystem", "RemoteDomain", "RemoteSite", learnedDomains, conflicts, requireDomain);
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
        string siteColumn,
        IDictionary<string, LearnedComputerDomain> learnedDomains,
        ISet<string> conflicts,
        bool requireDomain)
    {
        var system = GetRowString(values, columnIndexes, systemColumn);
        var domain = GetRowString(values, columnIndexes, domainColumn);
        if (!AlertRecord.IsMeaningfulTarget(system) || (requireDomain && !AlertRecord.IsMeaningfulTarget(domain)))
        {
            return;
        }

        var computer = AlertRecord.GetTargetName(system).Trim();
        domain = domain.Trim();
        var site = GetRowString(values, columnIndexes, siteColumn).Trim();
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

        if (existing is not null && !string.Equals(existing.Site, site, StringComparison.OrdinalIgnoreCase))
        {
            site = string.Empty;
        }
        learnedDomains[computer] = new LearnedComputerDomain(computer, domain,
            AlertRecord.IsMeaningfulTarget(site) && site.Length <= 256 ? site : null);
    }

    public static ComputerInventoryEntry? FindExistingDcPortTarget(
        AlertRecord alert, IReadOnlyList<ComputerInventoryEntry> inventory)
    {
        var fact = LearnSystems(alert.RawJson, requireDomain: false).FirstOrDefault(item =>
            item.Computer.Equals(alert.TargetName, StringComparison.OrdinalIgnoreCase));
        return fact is null ? null : FindExistingSystem(alert, fact, inventory);
    }

    private static ComputerInventoryEntry? FindExistingSystem(
        AlertRecord alert, LearnedComputerDomain fact, IReadOnlyList<ComputerInventoryEntry> inventory)
    {
        // Cross-subscription matches require an explicit matching domain, never just a computer name.
        var candidates = inventory.Where(entry =>
            entry.Computer.Equals(fact.Computer, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(fact.Domain)
                ? entry.SubscriptionId.Equals(alert.SubscriptionId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(entry.Domain, fact.Domain, StringComparison.OrdinalIgnoreCase) ||
                  (string.IsNullOrWhiteSpace(entry.Domain) &&
                   entry.SubscriptionId.Equals(alert.SubscriptionId, StringComparison.OrdinalIgnoreCase)))).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
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

}

public sealed record LearnedComputerDomain(string Computer, string Domain, string? Site = null);