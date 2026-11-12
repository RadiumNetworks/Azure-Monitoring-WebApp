using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class HistoricalUserQueries
{
    internal static IQueryable<DirectoryUser> Profiles(AlertDbContext context) =>
        context.DirectoryUsers.Where(user => user.Origin == DirectoryUserOrigins.Legacy && !user.UsesStableIdentity &&
            !context.SqlUserIdentities.Any(identity => identity.UserId == user.Id) &&
            !context.EntraUserIdentities.Any(identity => identity.UserId == user.Id) &&
            !context.ScimUserStates.Any(state => state.UserId == user.Id));

    internal static async Task<string[]> AliasesAsync(AlertDbContext context, CancellationToken token)
    {
        var values = await context.AnalysisDashboards.Select(row => row.OwnerKey)
            .Union(context.InboxLayouts.Select(row => row.OwnerKey))
            .Union(context.AnalysisDashboardTemplates.Select(row => row.UpdatedBy))
            .Union(context.AgentAnalysisJobs.Select(row => row.CreatedBy))
            .Union(context.LogbookEntries.Select(row => row.User))
            .Union(context.RootCauseAnalyses.Select(row => row.AssignedTo))
            .Union(context.RcaChanges.Select(row => row.Actor))
            .Union(context.AnalysisQueryDefinitions.Select(row => row.UpdatedBy))
            .Union(context.AnalysisQueryVersions.Select(row => row.PublishedBy))
            .Union(context.AgentAnalysisReviewEvents.Select(row => row.Actor))
            .ToArrayAsync(token);
        var assignments = await context.RcaChanges.Where(row => row.Field == "AssignedTo")
            .Select(row => new { row.Before, row.After }).ToArrayAsync(token);
        return values.Concat(assignments.SelectMany(row => new[] { row.Before, row.After }))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0 && !value.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static async Task<HashSet<string>> ReservedAliasesAsync(AlertDbContext context, CancellationToken token)
    {
        var sql = await context.AuthenticationUsers.Select(user => user.Username).ToArrayAsync(token);
        var confirmed = await context.LegacyUserLinks.Where(link => link.Status == LegacyUserLinkStatuses.Confirmed)
            .Select(link => link.Alias).ToArrayAsync(token);
        return sql.Concat(confirmed).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
