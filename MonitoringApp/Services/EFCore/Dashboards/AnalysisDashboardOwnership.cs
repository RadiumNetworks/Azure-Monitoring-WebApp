using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed record AnalysisDashboardOwner(string OwnerKey, string? DisplayName);

internal static class AnalysisDashboardOwnership
{
    internal static async Task<HashSet<string>> AllowedOwnersAsync(
        AlertDbContext context, string actor, IdentityDirectoryOptions? options, CancellationToken token)
    {
        await IdentityOwnerGuard.ValidateAsync(context, actor, options, token);
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { actor };
        if (options?.Enabled != true || DirectoryIdentity.TryReadUserId(actor, out _) ||
            actor.Equals("open-access", StringComparison.OrdinalIgnoreCase) ||
            actor.Equals("open-mode-operator", StringComparison.OrdinalIgnoreCase))
            return owners;

        var reserved = await HistoricalUserQueries.ReservedAliasesAsync(context, token);
        var profiles = await HistoricalUserQueries.Profiles(context).AsNoTracking()
            .Where(user => user.IsEnabled).ToArrayAsync(token);
        var main = profiles.SingleOrDefault(user =>
            user.UserName.Equals(actor, StringComparison.OrdinalIgnoreCase) && !reserved.Contains(user.UserName));
        if (main is null)
            return owners;
        var links = await context.HistoricalUserAliases.AsNoTracking().ToArrayAsync(token);
        var aliases = links.Where(link => link.MainUserId == main.Id).Select(link => link.AliasUserId).ToHashSet();
        if (aliases.Count == 0)
            return owners;
        if (links.Any(link => link.AliasUserId == main.Id || aliases.Contains(link.MainUserId)))
            throw new InvalidOperationException("Historical dashboard groups must not contain chains or cycles.");
        owners.UnionWith(profiles.Where(user => aliases.Contains(user.Id) && !reserved.Contains(user.UserName))
            .Select(user => user.UserName));
        return owners;
    }

    internal static async Task RequireAsync(AlertDbContext context, string actor, string owner,
        IdentityDirectoryOptions? options, CancellationToken token)
    {
        if (actor.Equals(owner, StringComparison.OrdinalIgnoreCase))
            await IdentityOwnerGuard.ValidateAsync(context, actor, options, token);
        else if (!(await AllowedOwnersAsync(context, actor, options, token)).Contains(owner))
            throw new UnauthorizedAccessException("Only the owner or its current historical main account can edit this dashboard.");
    }
}
