using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace MonitoringApp;

/// <summary>
/// Resolves the interactive user's display name from ASP.NET or Azure App Service Authentication.
/// </summary>
public sealed class CurrentUserIdentity(IHttpContextAccessor httpContextAccessor, IdentityDirectoryService? directory = null)
{
    private string? cachedDisplayName;

    public bool DirectoryEnabled => directory?.Enabled == true;

    private static readonly string[] PreferredClaimTypes =
    [
        "preferred_username",
        "emails",
        ClaimTypes.Email,
        ClaimTypes.Upn,
        ClaimTypes.Name,
        "name"
    ];

    public string? GetDisplayName(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var claimValue = PreferredClaimTypes
            .Select(type => principal.FindFirst(type)?.Value)
            .FirstOrDefault(IsUsable);
        if (claimValue is not null)
        {
            return Cache(claimValue);
        }

        if (IsUsable(principal.Identity?.Name))
        {
            return Cache(principal.Identity!.Name!);
        }

        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null)
        {
            return cachedDisplayName;
        }

        var appServiceName = headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();
        if (IsUsable(appServiceName))
        {
            return Cache(appServiceName!);
        }

        var principalName = TryReadAppServicePrincipal(
            headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault());
        return principalName is null ? cachedDisplayName : Cache(principalName);
    }

    public string GetActor(ClaimsPrincipal principal, bool openAuthentication) =>
        DirectoryIdentity.Actor(principal) ?? GetDisplayName(principal) ??
        (openAuthentication
            ? "open-mode-operator"
            : throw new InvalidOperationException("The authenticated user has no display name."));

    public async Task<string> GetActorAsync(ClaimsPrincipal principal, bool openAuthentication, CancellationToken cancellationToken = default)
    {
        var resolved = directory is null ? principal : await directory.ResolveAsync(principal, cancellationToken);
        return GetActor(resolved, openAuthentication);
    }

    public static ClaimsPrincipal? CreateAppServicePrincipal(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var encoded = headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 64 * 1024)
            return null;

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("auth_typ", out var authenticationType) ||
                authenticationType.ValueKind != JsonValueKind.String ||
                authenticationType.GetString() != "aad" ||
                !root.TryGetProperty("claims", out var sourceClaims) ||
                sourceClaims.ValueKind != JsonValueKind.Array)
                return null;

            var roleType = root.TryGetProperty("role_typ", out var roleTypeElement) &&
                roleTypeElement.ValueKind == JsonValueKind.String
                    ? roleTypeElement.GetString()
                    : ClaimTypes.Role;
            var claims = new List<Claim>();
            foreach (var sourceClaim in sourceClaims.EnumerateArray())
            {
                if (sourceClaim.ValueKind != JsonValueKind.Object ||
                    !sourceClaim.TryGetProperty("typ", out var type) || type.ValueKind != JsonValueKind.String ||
                    !sourceClaim.TryGetProperty("val", out var value) || value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(type.GetString()) || string.IsNullOrWhiteSpace(value.GetString()))
                    return null;

                var claimType = type.GetString()!;
                claims.Add(new Claim(
                    claimType == roleType || claimType == "roles" ? ClaimTypes.Role : claimType,
                    value.GetString()!));
            }

            if (claims.Any(claim => claim.Type == "idtyp" && claim.Value == "app"))
                return null;

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AppServiceAuthenticationHandler.SchemeName));
            var name = root.TryGetProperty("name_typ", out var nameType) && nameType.ValueKind == JsonValueKind.String &&
                IsUsable(nameType.GetString())
                    ? principal.FindFirstValue(nameType.GetString()!)
                    : null;
            name = IsUsable(name)
                ? name
                : PreferredClaimTypes.Select(type => principal.FindFirstValue(type)).FirstOrDefault(IsUsable);
            if (name is null)
                return null;

            if (!claims.Any(claim => claim.Type == ClaimTypes.Name))
                principal.Identities.First().AddClaim(new Claim(ClaimTypes.Name, name.Trim()));

            var principalId = headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault() ??
                principal.FindFirstValue("oid") ??
                principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
            if (IsUsable(principalId) && !claims.Any(claim => claim.Type == ClaimTypes.NameIdentifier))
                principal.Identities.First().AddClaim(new Claim(ClaimTypes.NameIdentifier, principalId!));
            return principal;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? TryReadAppServicePrincipal(string? encodedPrincipal)
    {
        if (!IsUsable(encodedPrincipal))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPrincipal!));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("claims", out var claims) ||
                claims.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var preferredType in PreferredClaimTypes)
            {
                foreach (var claim in claims.EnumerateArray())
                {
                    if (claim.TryGetProperty("typ", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        preferredType.Equals(type.GetString(), StringComparison.OrdinalIgnoreCase) &&
                        claim.TryGetProperty("val", out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        IsUsable(value.GetString()))
                    {
                        return value.GetString()!.Trim();
                    }
                }
            }

            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsUsable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private string Cache(string value)
    {
        cachedDisplayName = value.Trim();
        return cachedDisplayName;
    }
}
