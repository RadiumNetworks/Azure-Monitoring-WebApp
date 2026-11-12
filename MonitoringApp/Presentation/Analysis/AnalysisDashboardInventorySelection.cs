namespace MonitoringApp;

public enum AnalysisDashboardInventoryStatus
{
    Ready,
    Empty,
    PartiallyMissing,
    AllMissing
}

public sealed record AnalysisDashboardInventorySelection(
    IReadOnlyList<AnalysisQuerySystem> Systems,
    IReadOnlyList<AnalysisDashboardSystem> MissingSystems)
{
    public AnalysisDashboardInventoryStatus Status => MissingSystems.Count > 0
        ? Systems.Count > 0 ? AnalysisDashboardInventoryStatus.PartiallyMissing : AnalysisDashboardInventoryStatus.AllMissing
        : Systems.Count > 0 ? AnalysisDashboardInventoryStatus.Ready : AnalysisDashboardInventoryStatus.Empty;

    public static AnalysisDashboardInventorySelection Resolve(
        IEnumerable<AnalysisDashboardSystem> savedSystems,
        IReadOnlyList<ComputerInventoryEntry> inventory)
    {
        var (systems, missing) = ResolveFixed(savedSystems, inventory);
        return new(systems.Select(system => new AnalysisQuerySystem(system.SubscriptionId, system.Computer)).ToArray(), missing);
    }

    public static AnalysisDashboardInventorySelection Resolve(
        AnalysisDashboardDocument document,
        IReadOnlyList<ComputerInventoryEntry> inventory,
        TargetWorkspaceResolver workspaceResolver,
        string? workspaceKey = null)
    {
        document.ValidateSelection();
        if (!string.IsNullOrEmpty(workspaceKey) && !workspaceResolver.IsConfiguredWorkspace(workspaceKey))
            throw new AnalysisValidationException(AnalysisValidationCode.WorkspaceUnavailable,
                "The selected Log Analytics workspace is not configured.");

        List<ComputerInventoryEntry> candidates;
        List<AnalysisDashboardSystem> missing;
        if (document.SelectionMode == AnalysisDashboardSelectionMode.FixedSystems)
            (candidates, missing) = ResolveFixed(document.Systems, inventory);
        else
        {
            var roles = document.InventoryRoles.Select(role => InventoryRoleRuleMatcher.NormalizeRole(role.Trim()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            candidates = inventory.Where(entry => entry.Role is { } role &&
                roles.Contains(InventoryRoleRuleMatcher.NormalizeRole(role.Trim()))).ToList();
            missing = [];
        }

        if (candidates.Any(system => string.IsNullOrWhiteSpace(system.SubscriptionId) || string.IsNullOrWhiteSpace(system.Computer)))
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardSelectionInvalid,
                "The matching inventory contains an invalid system identity.");
        var routed = candidates
            .Select(system => (System: system, Workspace: ResolveWorkspace(system, workspaceResolver)))
            .ToArray();
        if (routed.GroupBy(item => SubscriptionComputerKey.Create(item.System.SubscriptionId, item.System.Computer), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(item => item.Workspace.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardInventoryRoutingInvalid,
                "The same inventory system resolves to different configured workspaces.", [],
                new TargetWorkspaceResolutionException(TargetWorkspaceResolutionError.ConflictingRoutes,
                    "The same inventory system resolves to different configured workspaces."));
        var systems = routed
            .Where(item => string.IsNullOrEmpty(workspaceKey) ||
                item.Workspace.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => SubscriptionComputerKey.Create(item.System.SubscriptionId, item.System.Computer), StringComparer.OrdinalIgnoreCase)
            .Select(item => new AnalysisQuerySystem(item.System.SubscriptionId, item.System.Computer))
            .ToArray();
        if (systems.Length > AnalysisDashboardDocument.MaximumSystems)
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardSystemsLimitExceeded,
                "The dashboard selection matches more than 100 systems. Narrow the selection.");
        return new(Array.AsReadOnly(systems), missing.AsReadOnly());
    }

    private static CrossTenantLogAnalyticsWorkspaceOptions ResolveWorkspace(
        ComputerInventoryEntry system, TargetWorkspaceResolver workspaceResolver)
    {
        try { return workspaceResolver.Resolve(system.SubscriptionId, system.Domain).Workspace; }
        catch (TargetWorkspaceResolutionException exception)
        {
            throw new AnalysisValidationException(AnalysisValidationCode.DashboardInventoryRoutingInvalid,
                "The dashboard inventory cannot be assigned unambiguously to configured workspaces.", [], exception);
        }
    }

    private static (List<ComputerInventoryEntry> Systems, List<AnalysisDashboardSystem> Missing) ResolveFixed(
        IEnumerable<AnalysisDashboardSystem> savedSystems,
        IReadOnlyList<ComputerInventoryEntry> inventory)
    {
        var systems = new List<ComputerInventoryEntry>();
        var missing = new List<AnalysisDashboardSystem>();
        foreach (var saved in savedSystems)
        {
            var current = inventory.SingleOrDefault(entry =>
                entry.SubscriptionId.Equals(saved.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
                entry.Computer.Equals(saved.Computer, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                missing.Add(saved);
            else
                systems.Add(current);
        }
        return (systems, missing);
    }
}
