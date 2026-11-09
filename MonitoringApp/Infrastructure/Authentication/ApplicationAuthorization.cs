using Microsoft.AspNetCore.Authorization;

namespace MonitoringApp;

public static class ApplicationAuthorization
{
    public const string AccessPolicy = "ApplicationAccess";
    public const string OperatorPolicy = "OperatorAccess";
    public const string AdminPolicy = "AdminAccess";
    public const string SqlCookieScheme = "SqlAuthentication";

    public static void Configure(AuthorizationOptions options, ApplicationAuthenticationOptions authentication)
    {
        var access = CreatePolicy(authentication);
        if (authentication.IsEntra)
            access.RequireRole(SqlAuthenticationRoles.Reader, SqlAuthenticationRoles.Operator, SqlAuthenticationRoles.Admin);

        options.DefaultPolicy = access.Build();
        options.FallbackPolicy = authentication.IsOpen ? null : options.DefaultPolicy;
        options.AddPolicy(AccessPolicy, options.DefaultPolicy);

        var operatorAccess = CreatePolicy(authentication);
        var adminAccess = CreatePolicy(authentication);
        if (!authentication.IsOpen)
        {
            operatorAccess.RequireRole(SqlAuthenticationRoles.Operator, SqlAuthenticationRoles.Admin);
            adminAccess.RequireRole(SqlAuthenticationRoles.Admin);
        }
        options.AddPolicy(OperatorPolicy, operatorAccess.Build());
        options.AddPolicy(AdminPolicy, adminAccess.Build());
    }

    private static AuthorizationPolicyBuilder CreatePolicy(ApplicationAuthenticationOptions authentication) =>
        authentication.IsOpen
            ? new AuthorizationPolicyBuilder().RequireAssertion(_ => true)
            : new AuthorizationPolicyBuilder(authentication.IsEntra
                    ? AppServiceAuthenticationHandler.SchemeName
                    : SqlCookieScheme)
                .RequireAuthenticatedUser();
}
