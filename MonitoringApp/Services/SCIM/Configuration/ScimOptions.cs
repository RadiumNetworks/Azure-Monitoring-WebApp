namespace MonitoringApp;

public sealed class ScimOptions
{
    public const string SectionName = "Scim";
    public bool Enabled { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "Entra provisioning";
    public string BaseUrl { get; set; } = "";
    public string[] TokenHashes { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        if (!Enabled) return [];
        var errors = new List<string>();
        if (ConnectionId == Guid.Empty) errors.Add("Scim.ConnectionId is required.");
        if (TenantId == Guid.Empty) errors.Add("Scim.TenantId is required.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 256) errors.Add("Scim.Name must contain 1 to 256 characters.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !uri.AbsolutePath.TrimEnd('/').EndsWith("/scim/v2", StringComparison.Ordinal))
            errors.Add("Scim.BaseUrl must be an absolute HTTPS URL ending in /scim/v2 without credentials, query or fragment.");
        if (TokenHashes is null || TokenHashes.Length is < 1 or > 2 ||
            TokenHashes.Any(hash => hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            errors.Add("Scim.TokenHashes must contain one or two SHA-256 token hashes.");
        return errors;
    }

    public ProvisioningSource Source => new(ConnectionId, TenantId, Name);
}
