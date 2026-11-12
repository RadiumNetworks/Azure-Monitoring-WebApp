using Microsoft.AspNetCore.Authorization;

namespace MonitoringApp;

public sealed class DirectoryAccessRequirement : IAuthorizationRequirement;

public sealed class DirectoryAccessHandler(
    IdentityDirectoryService directory,
    ILogger<DirectoryAccessHandler> logger) : AuthorizationHandler<DirectoryAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, DirectoryAccessRequirement requirement)
    {
        try
        {
            await directory.ResolveAsync(context.User);
            context.Succeed(requirement);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Directory identity access was denied.");
            context.Fail(new AuthorizationFailureReason(this, "Directory identity access was denied."));
        }
    }
}
