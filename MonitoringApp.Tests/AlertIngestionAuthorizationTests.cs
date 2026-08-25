using System.Security.Claims;

namespace MonitoringApp.Tests;

public sealed class AlertIngestionAuthorizationTests
{
    private const string AllowedTenantId = "11111111-1111-1111-1111-111111111111";
    private const string AllowedClientId = "22222222-2222-2222-2222-222222222222";

    private static readonly AlertIngestionAuthenticationOptions Options = new()
    {
        Enabled = true,
        Audience = "33333333-3333-3333-3333-333333333333",
        Sources =
        [
            new AlertIngestionSource
            {
                TenantId = AllowedTenantId,
                ClientId = AllowedClientId
            }
        ]
    };

    [Fact]
    public void AllowsConfiguredAppIdentityWithRequiredRole()
    {
        var principal = CreatePrincipal(AllowedTenantId, AllowedClientId, "Alerts.Write", "app");

        Assert.True(AlertIngestionAuthorization.IsAuthorized(principal, Options));
    }

    [Fact]
    public void RejectsDifferentIdentityFromAllowedTenant()
    {
        var principal = CreatePrincipal(
            AllowedTenantId,
            "44444444-4444-4444-4444-444444444444",
            "Alerts.Write",
            "app");

        Assert.False(AlertIngestionAuthorization.IsAuthorized(principal, Options));
    }

    [Theory]
    [InlineData("55555555-5555-5555-5555-555555555555", AllowedClientId, "Alerts.Write", "app")]
    [InlineData(AllowedTenantId, AllowedClientId, "Alerts.Read", "app")]
    [InlineData(AllowedTenantId, AllowedClientId, "Alerts.Write", "user")]
    public void RejectsWrongTenantRoleOrTokenType(
        string tenantId,
        string clientId,
        string role,
        string identityType)
    {
        var principal = CreatePrincipal(tenantId, clientId, role, identityType);

        Assert.False(AlertIngestionAuthorization.IsAuthorized(principal, Options));
    }

    [Fact]
    public void RejectsDelegatedToken()
    {
        var principal = CreatePrincipal(AllowedTenantId, AllowedClientId, "Alerts.Write", "app", "alerts.write");

        Assert.False(AlertIngestionAuthorization.IsAuthorized(principal, Options));
    }

    [Fact]
    public void DisabledAuthenticationNeedsNoConfiguration()
    {
        var options = new AlertIngestionAuthenticationOptions();

        Assert.False(options.Enabled);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void EnabledAuthenticationRejectsMissingConfiguration()
    {
        var errors = new AlertIngestionAuthenticationOptions { Enabled = true }.Validate();

        Assert.Contains(errors, error => error.StartsWith("Audience", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("At least one", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsCompleteConfiguration()
    {
        Assert.Empty(Options.Validate());
    }

    private static ClaimsPrincipal CreatePrincipal(
        string tenantId,
        string clientId,
        string role,
        string identityType,
        string? scope = null)
    {
        var claims = new List<Claim>
        {
            new("iss", $"https://login.microsoftonline.com/{tenantId}/v2.0"),
            new("tid", tenantId),
            new("azp", clientId),
            new("roles", role),
            new("idtyp", identityType)
        };
        if (scope is not null)
        {
            claims.Add(new Claim("scp", scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }
}