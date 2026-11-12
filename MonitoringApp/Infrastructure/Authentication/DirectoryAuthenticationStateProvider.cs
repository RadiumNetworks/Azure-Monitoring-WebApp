using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace MonitoringApp;

public sealed class DirectoryAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IdentityDirectoryService directory,
    ILogger<DirectoryAuthenticationStateProvider> logger) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        try
        {
            await directory.ResolveAsync(authenticationState.User, cancellationToken);
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "The existing interactive session no longer has directory access.");
            return false;
        }
    }
}
