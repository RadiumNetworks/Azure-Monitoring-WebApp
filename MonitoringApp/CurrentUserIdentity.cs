using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace MonitoringApp;

/// <summary>
/// Resolves the interactive user's display name from ASP.NET or Azure App Service Authentication.
/// </summary>
public sealed class CurrentUserIdentity(IHttpContextAccessor httpContextAccessor)
{
    private string? cachedDisplayName;

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
        GetDisplayName(principal) ??
        (openAuthentication
            ? "open-mode-operator"
            : throw new InvalidOperationException("The authenticated user has no display name."));

    public static ClaimsPrincipal? CreateAppServicePrincipal(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var name = headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();
        if (!IsUsable(name))
        {
            name = TryReadAppServicePrincipal(
                headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault());
        }

        if (!IsUsable(name))
        {
            return null;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name!.Trim()),
            new("preferred_username", name.Trim())
        };
        var principalId = headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        if (IsUsable(principalId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, principalId!.Trim()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "AppServiceEntra"));
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
