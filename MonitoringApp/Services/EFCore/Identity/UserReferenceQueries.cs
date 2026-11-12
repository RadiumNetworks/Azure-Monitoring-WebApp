using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class UserReferenceQueries
{
    internal static IQueryable<DirectoryUser> AvailableUsers(AlertDbContext context) =>
        context.DirectoryUsers.Where(user => user.IsEnabled &&
            !context.ScimUserStates.Any(state => state.UserId == user.Id && (!state.IsActive || state.IsDeleted)) &&
            (user.Origin == DirectoryUserOrigins.Sql
                ? context.SqlUserIdentities.Any(binding => binding.UserId == user.Id &&
                    context.AuthenticationUsers.Any(account => account.Username == binding.Username && SqlAuthenticationRoles.All.Contains(account.Role)))
                : context.EntraUserIdentities.Any(binding => binding.UserId == user.Id)));

    internal static string Label(DirectoryUser user, string? reference = null)
    {
        var display = new[] { user.LocalDisplayName, user.DisplayName,
            string.Join(" ", new[] { user.GivenName, user.FamilyName }.Where(value => !string.IsNullOrWhiteSpace(value))),
            user.UserName, user.Email }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var key = reference ?? user.UserName;
        return display is null ? DirectoryIdentity.OwnerKey(user.Id) :
            string.IsNullOrWhiteSpace(key) || display.Equals(key, StringComparison.OrdinalIgnoreCase)
                ? display : $"{display} ({key})";
    }

    internal static async Task<IReadOnlyDictionary<string, string>> LabelsAsync(
        AlertDbContext context, IEnumerable<string> values, bool enabled, CancellationToken token, bool includeStableIdentities = true)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!enabled) return result;
        var keys = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length == 0) return result;
        var ids = includeStableIdentities ? keys.Select(value => DirectoryIdentity.TryReadUserId(value, out var id) ? (Guid?)id : null)
            .OfType<Guid>().Distinct().ToArray() : [];
        var users = await context.DirectoryUsers.AsNoTracking().Where(user => ids.Contains(user.Id)).ToArrayAsync(token);
        // A historical profile labels only its original alias, never a new stable identity.
        var historical = await HistoricalUserQueries.Profiles(context).AsNoTracking().ToArrayAsync(token);
        var historicalIds = historical.Select(user => user.Id).ToHashSet();
        var reserved = historical.Length == 0 ? new HashSet<string>() :
            await HistoricalUserQueries.ReservedAliasesAsync(context, token);
        var profiles = historical.Where(user => !reserved.Contains(user.UserName))
            .ToDictionary(user => user.UserName, StringComparer.OrdinalIgnoreCase);
        var profilesById = profiles.Values.ToDictionary(user => user.Id);
        var links = historical.Length == 0 ? [] : await context.HistoricalUserAliases.AsNoTracking().ToArrayAsync(token);
        var mainByAlias = links.Where(link => profilesById.ContainsKey(link.AliasUserId) && profilesById.ContainsKey(link.MainUserId))
            .ToDictionary(link => link.AliasUserId, link => link.MainUserId);
        if (mainByAlias.Values.Any(mainByAlias.ContainsKey))
            throw new InvalidOperationException("Historical display groups must not contain chains or cycles.");
        foreach (var key in keys)
        {
            if (DirectoryIdentity.TryReadUserId(key, out var id))
            {
                var user = users.SingleOrDefault(user => user.Id == id && !historicalIds.Contains(id));
                if (user is not null) result[key] = Label(user);
            }
            else if (profiles.TryGetValue(key.Trim(), out var profile))
                result[key] = Label(mainByAlias.TryGetValue(profile.Id, out var mainId) ? profilesById[mainId] : profile, key.Trim());
        }
        return result;
    }

    internal static async Task<HashSet<string>> UnavailableNamesAsync(AlertDbContext context, CancellationToken token)
    {
        var available = AvailableUsers(context);
        var historical = HistoricalUserQueries.Profiles(context);
        var unavailable = await context.DirectoryUsers.AsNoTracking()
            .Where(user => !available.Any(candidate => candidate.Id == user.Id) &&
                !historical.Any(profile => profile.Id == user.Id)).ToArrayAsync(token);
        var ids = unavailable.Select(user => user.Id).ToArray();
        var aliases = await context.LegacyUserLinks.AsNoTracking()
            .Where(link => link.TargetUserId.HasValue && ids.Contains(link.TargetUserId.Value) && link.Status == LegacyUserLinkStatuses.Confirmed)
            .Select(link => link.Alias).ToArrayAsync(token);
        return unavailable.SelectMany(user => new[] { user.UserName, user.Email ?? "" }).Concat(aliases)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
