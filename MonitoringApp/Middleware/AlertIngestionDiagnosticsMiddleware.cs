using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace MonitoringApp.Middleware;

public sealed class AlertIngestionDiagnosticsMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(
        HttpContext context,
        IAlertIngestionDiagnostics diagnostics,
        AlertIngestionAuthenticationOptions options)
    {
        if (context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(data => data.Policy == AlertIngestionAuthorization.PolicyName) == true)
        {
            // Also observe 401/403 responses that never reach an endpoint handler.
            context.Response.OnCompleted(async () =>
            {
                var authentication = options.Enabled
                    ? await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme)
                    : null;
                diagnostics.Record(context, authentication);
            });
        }

        return next(context);
    }
}
