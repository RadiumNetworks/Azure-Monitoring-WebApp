using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class IdentityOwnerGuard
{
    public static async Task ValidateAsync(AlertDbContext context, string owner, IdentityDirectoryOptions? options, CancellationToken cancellationToken)
    {
        if (options?.Enabled != true)
            return;
        if (owner.StartsWith("user:", StringComparison.Ordinal))
        {
            if (!Guid.TryParse(owner.AsSpan(5), out var userId) ||
                !await context.DirectoryUsers.AnyAsync(user => user.Id == userId && user.IsEnabled && user.UsesStableIdentity, cancellationToken) ||
                await context.ScimUserStates.AnyAsync(state => state.UserId == userId && !state.IsActive, cancellationToken))
                throw new UnauthorizedAccessException("The directory owner is unavailable or disabled.");
        }
        else if (await context.LegacyUserLinks.AnyAsync(link =>
            link.Alias == owner && link.Status == LegacyUserLinkStatuses.Confirmed, cancellationToken))
            throw new IdentityMigrationException("IdentityMigration.Errors.ReloadEditor");
    }
}
