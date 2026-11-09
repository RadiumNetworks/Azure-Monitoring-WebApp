namespace MonitoringApp;

internal sealed record AnalysisQueryWorkspaceValidation(
    string WorkspaceKey, string Status, string Message, DateTimeOffset CheckedAt);

internal static class AnalysisQueryValidation
{
    internal static async Task<IReadOnlyList<AnalysisQueryWorkspaceValidation>> ExecuteAsync(
        AnalysisQueryDefinition query, IReadOnlyList<ComputerInventoryEntry> inventory,
        CrossTenantLogAnalyticsOptions options, AnalysisQueryRunner runner, CancellationToken cancellationToken)
    {
        AnalysisQueryValidator.RequireValidQuery(query);

        var validations = new List<AnalysisQueryWorkspaceValidation>();
        foreach (var assignment in query.Workspaces.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = options.Workspaces.Single(item =>
                item.Key.Equals(assignment.WorkspaceKey, StringComparison.OrdinalIgnoreCase));
            var sample = inventory.FirstOrDefault(item => ResolvesTo(item, workspace.Key));
            if (sample is null)
            {
                validations.Add(new(assignment.WorkspaceKey, AnalysisQueryValidationStatuses.Failed,
                    "No inventory system routes to this workspace.", DateTimeOffset.UtcNow));
                continue;
            }

            var prepared = KqlQueryBuilder.PrepareInteractive(workspace, query.Kql, [sample.Computer],
                query.DefaultBinMinutes, Math.Min(query.DefaultTimeRangeMinutes, 60),
                visualizationType: query.VisualizationType);
            var result = await runner.TryQueryAsync(prepared, cancellationToken);
            validations.Add(new(assignment.WorkspaceKey,
                result.Error is null ? AnalysisQueryValidationStatuses.Succeeded : AnalysisQueryValidationStatuses.Failed,
                result.Error ?? $"Validated with {result.Tables?.Sum(table => table.Rows.Count) ?? result.Rows.Count} row(s).",
                DateTimeOffset.UtcNow));
        }
        return validations;

        bool ResolvesTo(ComputerInventoryEntry entry, string workspaceKey)
        {
            try
            {
                return runner.WorkspaceResolver.Resolve(entry.SubscriptionId, entry.Domain).Workspace.Key
                    .Equals(workspaceKey, StringComparison.OrdinalIgnoreCase);
            }
            catch (TargetWorkspaceResolutionException)
            {
                return false;
            }
        }
    }
}
