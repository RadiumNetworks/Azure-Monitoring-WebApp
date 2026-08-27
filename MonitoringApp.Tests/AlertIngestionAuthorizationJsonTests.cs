using System.Security.Claims;

namespace MonitoringApp.Tests;

public sealed class AlertIngestionAuthorizationJsonTests
{
    private static readonly AuthorizationTestCases Cases =
        TestCaseLoader.Load<AuthorizationTestCases>("alert-ingestion-authorization.json");
    public static IEnumerable<object[]> PrincipalCaseIndexes => Indexes(Cases.Principals.Count);

    [Theory]
    [MemberData(nameof(PrincipalCaseIndexes))]
    public void AuthorizesPrincipalsAccordingToJsonCases(int caseIndex)
    {
        var testCase = Cases.Principals[caseIndex];
        var actual = AlertIngestionAuthorization.IsAuthorized(CreatePrincipal(testCase), Cases.Options);
        Assert.Equal(testCase.ExpectedAuthorized, actual);
    }

    [Fact]
    public void DisabledAuthenticationNeedsNoConfiguration()
    {
        var options = new AlertIngestionAuthenticationOptions();
        Assert.False(options.Enabled);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void EnabledAuthenticationReportsJsonConfiguredMissingFields()
    {
        var errors = new AlertIngestionAuthenticationOptions { Enabled = true }.Validate();
        foreach (var prefix in Cases.MissingConfigurationErrorPrefixes)
        {
            Assert.Contains(errors, error => error.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CompleteJsonConfigurationIsValid()
    {
        Assert.Empty(Cases.Options.Validate());
    }

    private static ClaimsPrincipal CreatePrincipal(PrincipalAuthorizationCase source)
    {
        var claims = new List<Claim>
        {
            new("iss", $"https://login.microsoftonline.com/{source.TenantId}/v2.0"),
            new("tid", source.TenantId),
            new("azp", source.ClientId),
            new("roles", source.Role),
            new("idtyp", source.IdentityType)
        };
        if (source.Scope is not null)
        {
            claims.Add(new Claim("scp", source.Scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}