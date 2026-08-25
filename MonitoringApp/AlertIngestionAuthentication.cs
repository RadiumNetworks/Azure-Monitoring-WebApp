using System.Security.Claims;

namespace MonitoringApp;

/// <summary>
/// Holds the configuration used to authenticate alert-ingestion requests. Authentication remains disabled until the Enabled setting is explicitly set.
/// </summary>
public sealed class AlertIngestionAuthenticationOptions
{
    public const string SectionName = "AlertIngestionAuthentication";

    public bool Enabled { get; set; }
    public string Audience { get; set; } = string.Empty;
    public string RequiredRole { get; set; } = "Alerts.Write";
    public List<AlertIngestionSource> Sources { get; set; } = [];

    /// <summary>
    /// Checks that all required authentication settings are valid when authentication is enabled. It returns an empty list when authentication is disabled or the configuration is valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        if (!Enabled)
        {
            return [];
        }

        var errors = new List<string>();
        if (!Guid.TryParse(Audience, out _))
        {
            errors.Add("Audience must be the API application's client ID GUID.");
        }

        if (string.IsNullOrWhiteSpace(RequiredRole))
        {
            errors.Add("RequiredRole is required.");
        }

        if (Sources.Count == 0)
        {
            errors.Add("At least one allowed tenant and managed identity pair is required.");
        }

        for (var index = 0; index < Sources.Count; index++)
        {
            if (!Guid.TryParse(Sources[index].TenantId, out _))
            {
                errors.Add($"Sources:{index}:TenantId must be a GUID.");
            }

            if (!Guid.TryParse(Sources[index].ClientId, out _))
            {
                errors.Add($"Sources:{index}:ClientId must be the UAMI client ID GUID.");
            }
        }

        return errors;
    }
}

/// <summary>
/// Identifies one tenant and user-assigned managed identity that may submit alerts. Both identifiers are expected to be client-facing GUID values.
/// </summary>
public sealed class AlertIngestionSource
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// Evaluates Microsoft Entra token claims for the alert-ingestion endpoint. It accepts only configured app-only managed identities with the required role.
/// </summary>
public static class AlertIngestionAuthorization
{
    /// <summary>
    /// Checks whether a claims principal represents an allowed tenant and managed-identity pair. The token must also have the expected issuer, app-only type, and application role.
    /// </summary>
    public static bool IsAuthorized(
        ClaimsPrincipal principal,
        AlertIngestionAuthenticationOptions options)
    {
        var tenantId = principal.FindFirstValue("tid");
        var clientId = principal.FindFirstValue("azp");
        var issuer = principal.FindFirstValue("iss");
        var expectedIssuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";

        return principal.FindFirstValue("idtyp") == "app" &&
            expectedIssuer.Equals(issuer, StringComparison.OrdinalIgnoreCase) &&
            principal.Claims.Any(claim => claim.Type == "roles" && claim.Value == options.RequiredRole) &&
            !principal.HasClaim(claim => claim.Type == "scp") &&
            options.Sources.Any(source =>
                source.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) &&
                source.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase));
    }
}