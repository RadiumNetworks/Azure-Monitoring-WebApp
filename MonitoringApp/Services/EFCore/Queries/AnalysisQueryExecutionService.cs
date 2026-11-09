using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class AnalysisQueryExecutionService(
    IDbContextFactory<AlertDbContext> contextFactory,
    AnalysisQueryRunner runner)
{
    public async Task<AnalysisQueryResponse> ExecuteAsync(
        AnalysisQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        AnalysisQueryDefinition query;
        ComputerInventoryEntry[] selected;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            query = await context.AnalysisQueryDefinitions
                .AsNoTracking()
                .Include(item => item.Workspaces)
                .Include(item => item.Versions)
                .SingleOrDefaultAsync(item =>
                    item.Id == request.QueryDefinitionId &&
                    item.Enabled &&
                    item.PublishedVersionId != null,
                    cancellationToken)
                ?? throw new KeyNotFoundException("The published analysis query was not found.");
            query = AnalysisQueryRegistry.CreatePublishedDefinition(query);

            var requestedSystems = request.Systems
                .Select(system => new AnalysisQuerySystem(system.SubscriptionId.Trim(), system.Computer.Trim()))
                .Distinct()
                .ToArray();
            if (requestedSystems.Length is < 1 or > 100)
                throw new AnalysisValidationException(AnalysisValidationCode.DistinctSystemsRequired,
                    "Select between 1 and 100 distinct systems.");
            selected = await AnalysisQueryInventory.LoadSelectedAsync(context, requestedSystems, cancellationToken);
        }

        return await runner.ExecuteAsync(
            query,
            selected,
            request.TimeRangeMinutes,
            cancellationToken,
            request.BinMinutes,
            request.GroupBy,
            request.WorkspaceKey,
            request.Domain,
            request.Site,
            request.Role);
    }

    public static bool IsRoleAllowed(string minimumRole, System.Security.Claims.ClaimsPrincipal user, bool openAuthentication)
    {
        if (openAuthentication)
            return true;
        var required = RoleRank(minimumRole);
        return SqlAuthenticationRoles.All
            .Where(role => RoleRank(role) >= required)
            .Any(user.IsInRole);
    }

    private static int RoleRank(string role) => role switch
    {
        SqlAuthenticationRoles.Admin => 3,
        SqlAuthenticationRoles.Operator => 2,
        _ => 1
    };
}
