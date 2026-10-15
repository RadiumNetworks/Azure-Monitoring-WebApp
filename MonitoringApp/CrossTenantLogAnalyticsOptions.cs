namespace MonitoringApp;

public sealed class CrossTenantLogAnalyticsOptions
{
    public const string SectionName = "CrossTenantLogAnalytics";

    public bool Enabled { get; init; }
    public string ManagedIdentityClientId { get; init; } = string.Empty;
    public string KeyVaultUri { get; init; } = string.Empty;
    public string ClientSecretName { get; init; } = string.Empty;
    public string ApplicationClientId { get; init; } = string.Empty;
    public int DiagnosticTimeoutSeconds { get; init; } = 30;
    public CrossTenantLogAnalyticsWorkspaceOptions[] Workspaces { get; init; } = [];

    public IReadOnlyList<string> Validate()
    {
        if (!Enabled)
        {
            return [];
        }

        var errors = new List<string>();
        ValidateGuid(ManagedIdentityClientId, nameof(ManagedIdentityClientId), errors);
        ValidateGuid(ApplicationClientId, nameof(ApplicationClientId), errors);

        if (!Uri.TryCreate(KeyVaultUri, UriKind.Absolute, out var keyVaultUri) ||
            keyVaultUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(keyVaultUri.UserInfo) ||
            !string.IsNullOrEmpty(keyVaultUri.Query) ||
            !string.IsNullOrEmpty(keyVaultUri.Fragment))
        {
            errors.Add($"{SectionName}:{nameof(KeyVaultUri)} must be an absolute HTTPS URI without credentials, query, or fragment.");
        }
        if (string.IsNullOrWhiteSpace(ClientSecretName) ||
            ClientSecretName.Length > 127 ||
            ClientSecretName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            errors.Add($"{SectionName}:{nameof(ClientSecretName)} must contain 1-127 letters, digits, or hyphens.");
        }
        if (DiagnosticTimeoutSeconds is < 5 or > 120)
        {
            errors.Add($"{SectionName}:{nameof(DiagnosticTimeoutSeconds)} must be between 5 and 120.");
        }
        if (Workspaces.Length == 0)
        {
            errors.Add($"{SectionName}:{nameof(Workspaces)} must contain at least one workspace.");
        }

        for (var index = 0; index < Workspaces.Length; index++)
        {
            ValidateWorkspace(Workspaces[index], index, errors);
        }

        AddDuplicateErrors(
            Workspaces.Select(workspace => workspace.Key.Trim()),
            "workspace keys",
            errors);
        AddDuplicateErrors(
            Workspaces.SelectMany(workspace => workspace.SubscriptionIds)
                .Select(NormalizeSubscriptionId)
                .Where(value => value is not null)!,
            "subscription IDs",
            errors);
        AddDuplicateErrors(
            Workspaces.SelectMany(workspace => workspace.Domains)
                .Select(NormalizeDomain)
                .Where(value => value.Length > 0),
            "domains",
            errors);

        return errors;
    }

    public static string? NormalizeSubscriptionId(string? value) =>
        Guid.TryParse(value?.Trim(), out var parsed) ? parsed.ToString("D") : null;

    public static string NormalizeDomain(string? value) =>
        value?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;

    private static void ValidateWorkspace(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        int index,
        ICollection<string> errors)
    {
        var path = $"{SectionName}:Workspaces:{index}";
        if (string.IsNullOrWhiteSpace(workspace.Key))
        {
            errors.Add($"{path}:Key must not be empty.");
        }
        if (string.IsNullOrWhiteSpace(workspace.DisplayName))
        {
            errors.Add($"{path}:DisplayName must not be empty.");
        }
        ValidateGuid(workspace.TenantId, $"{path}:TenantId", errors, includeSection: false);
        ValidateGuid(workspace.WorkspaceId, $"{path}:WorkspaceId", errors, includeSection: false);
        if (workspace.SubscriptionIds.Length == 0 && workspace.Domains.Length == 0)
        {
            errors.Add($"{path} must contain at least one subscription ID or domain.");
        }
        for (var subscriptionIndex = 0; subscriptionIndex < workspace.SubscriptionIds.Length; subscriptionIndex++)
        {
            if (NormalizeSubscriptionId(workspace.SubscriptionIds[subscriptionIndex]) is null)
            {
                errors.Add($"{path}:SubscriptionIds:{subscriptionIndex} must be a GUID.");
            }
        }
        for (var domainIndex = 0; domainIndex < workspace.Domains.Length; domainIndex++)
        {
            if (string.IsNullOrEmpty(NormalizeDomain(workspace.Domains[domainIndex])))
            {
                errors.Add($"{path}:Domains:{domainIndex} must not be empty.");
            }
        }
    }

    private static void ValidateGuid(
        string value,
        string name,
        ICollection<string> errors,
        bool includeSection = true)
    {
        if (!Guid.TryParse(value, out _))
        {
            errors.Add($"{(includeSection ? $"{SectionName}:" : string.Empty)}{name} must be a GUID.");
        }
    }

    private static void AddDuplicateErrors(
        IEnumerable<string?> values,
        string label,
        ICollection<string> errors)
    {
        var duplicates = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicates.Length > 0)
        {
            errors.Add(
                $"{SectionName} {label} must be unique across workspaces: {string.Join(", ", duplicates)}.");
        }
    }
}

public sealed class CrossTenantLogAnalyticsWorkspaceOptions
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string[] SubscriptionIds { get; init; } = [];
    public string[] Domains { get; init; } = [];
}
