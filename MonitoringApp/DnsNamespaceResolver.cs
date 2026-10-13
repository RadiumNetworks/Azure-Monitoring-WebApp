namespace MonitoringApp;

/// <summary>
/// Derives visual DNS namespace groups from known inventory domains without implying an AD forest or trust relationship.
/// </summary>
public static class DnsNamespaceResolver
{
    public static IReadOnlyDictionary<string, string> Resolve(IEnumerable<string> domains)
    {
        var knownDomains = domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim().TrimEnd('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var namespaceRoots = knownDomains
            .Where(candidate => knownDomains.Any(domain => IsChildOf(domain, candidate)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return knownDomains.ToDictionary(
            domain => domain,
            domain => namespaceRoots.Contains(domain)
                ? domain
                : namespaceRoots
                    .Where(candidate => IsChildOf(domain, candidate))
                    .OrderByDescending(candidate => candidate.Count(character => character == '.'))
                    .ThenByDescending(candidate => candidate.Length)
                    .FirstOrDefault() ?? domain,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsChildOf(string domain, string candidate) =>
        domain.Length > candidate.Length &&
        domain.EndsWith($".{candidate}", StringComparison.OrdinalIgnoreCase);
}