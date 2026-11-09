using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MonitoringApp;

public sealed class AppServiceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AppServiceEntra";

    public static void ValidateHosting(IConfiguration configuration)
    {
        if (!IsTrustedHost(configuration))
            throw new InvalidOperationException(
                "Authentication type 'entra' requires Azure App Service with Easy Auth enabled " +
                "(WEBSITE_SITE_NAME and WEBSITE_AUTH_ENABLED=true). Configure Entra authentication before switching modes.");
    }

    private static bool IsTrustedHost(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["WEBSITE_SITE_NAME"]) &&
        bool.TryParse(configuration["WEBSITE_AUTH_ENABLED"], out var enabled) && enabled;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!IsTrustedHost(configuration))
        {
            Logger.LogError("App Service identity headers were rejected outside an Easy Auth enabled App Service.");
            return Task.FromResult(AuthenticateResult.Fail("Trusted App Service authentication is required."));
        }

        if (!Request.Headers.ContainsKey("X-MS-CLIENT-PRINCIPAL"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = CurrentUserIdentity.CreateAppServicePrincipal(Request.Headers);
        if (principal is null)
        {
            Logger.LogWarning("App Service authentication supplied an invalid or non-user Entra principal.");
            return Task.FromResult(AuthenticateResult.Fail("A valid Entra user principal is required."));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Request.Path.StartsWithSegments("/api"))
            Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            Response.Redirect("/.auth/login/aad?post_login_redirect_uri=" +
                Uri.EscapeDataString(Request.PathBase + Request.Path + Request.QueryString));
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (Request.Path.StartsWithSegments("/api"))
            Response.StatusCode = StatusCodes.Status403Forbidden;
        else
            Response.Redirect("/access-denied");
        return Task.CompletedTask;
    }
}
