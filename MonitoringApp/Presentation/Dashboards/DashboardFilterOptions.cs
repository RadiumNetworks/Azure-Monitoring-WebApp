using System.Text.Json.Serialization;

namespace MonitoringApp;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardFilterOptions
{
    public const string SectionName = "DashboardFilters";

    public string? Workspace { get; init; }
    public string? Domain { get; init; }
    public string? Site { get; init; }
    public string? Role { get; init; }

    public IReadOnlyList<string> Validate(CrossTenantLogAnalyticsOptions options)
    {
        var errors = new List<string>();
        foreach (var (name, value) in new[] { (nameof(Workspace), Workspace), (nameof(Domain), Domain), (nameof(Site), Site), (nameof(Role), Role) })
        {
            if (value is not null && (value.Length > 256 || value.Length > 0 && string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
                errors.Add($"{name} must be null, empty, or contain 1-256 characters without control characters.");
        }
        var workspace = Workspace is null
            ? options.Workspaces.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : options.Workspaces.SingleOrDefault(item => item.Key.Equals(Workspace.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(Workspace) && workspace is null)
            errors.Add("Workspace must match a configured workspace key; use null for the first workspace or an empty string for All workspaces.");
        var domain = CrossTenantLogAnalyticsOptions.NormalizeDomain(Domain);
        if (!string.IsNullOrEmpty(Domain) && domain.Length == 0)
            errors.Add("Domain must contain a domain name, or be null or empty for All domains.");
        if (domain.Length > 0 && workspace is not null &&
            options.Workspaces.Any(item => item.Domains.Any(value => CrossTenantLogAnalyticsOptions.NormalizeDomain(value) == domain)) &&
            !workspace.Domains.Any(value => CrossTenantLogAnalyticsOptions.NormalizeDomain(value) == domain))
            errors.Add("Domain belongs to a different configured workspace.");
        return errors;
    }

    public string ResolveWorkspace(CrossTenantLogAnalyticsOptions options)
    {
        var errors = Validate(options);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid dashboard filter defaults: {string.Join(" ", errors)}");
        return Workspace is null
            ? options.Workspaces.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Key ?? string.Empty
            : Workspace.Length == 0 ? string.Empty
            : options.Workspaces.Single(item => item.Key.Equals(Workspace.Trim(), StringComparison.OrdinalIgnoreCase)).Key;
    }
}
