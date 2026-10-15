namespace MonitoringApp;

public sealed class TargetWorkspaceResolver
{
    private readonly IReadOnlyDictionary<string, CrossTenantLogAnalyticsWorkspaceOptions> subscriptions;
    private readonly IReadOnlyDictionary<string, CrossTenantLogAnalyticsWorkspaceOptions> domains;

    public TargetWorkspaceResolver(CrossTenantLogAnalyticsOptions options)
    {
        subscriptions = options.Workspaces
            .SelectMany(workspace => workspace.SubscriptionIds.Select(subscriptionId => new
            {
                Key = CrossTenantLogAnalyticsOptions.NormalizeSubscriptionId(subscriptionId)!,
                Workspace = workspace
            }))
            .ToDictionary(item => item.Key, item => item.Workspace, StringComparer.OrdinalIgnoreCase);
        domains = options.Workspaces
            .SelectMany(workspace => workspace.Domains.Select(domain => new
            {
                Key = CrossTenantLogAnalyticsOptions.NormalizeDomain(domain),
                Workspace = workspace
            }))
            .ToDictionary(item => item.Key, item => item.Workspace, StringComparer.OrdinalIgnoreCase);
    }

    public TargetWorkspaceResolution Resolve(string? subscriptionId, string? domain)
    {
        CrossTenantLogAnalyticsWorkspaceOptions? subscriptionWorkspace = null;
        var normalizedSubscriptionId =
            CrossTenantLogAnalyticsOptions.NormalizeSubscriptionId(subscriptionId);
        if (normalizedSubscriptionId is not null)
        {
            subscriptions.TryGetValue(normalizedSubscriptionId, out subscriptionWorkspace);
        }

        CrossTenantLogAnalyticsWorkspaceOptions? domainWorkspace = null;
        var normalizedDomain = CrossTenantLogAnalyticsOptions.NormalizeDomain(domain);
        if (normalizedDomain.Length > 0)
        {
            domains.TryGetValue(normalizedDomain, out domainWorkspace);
        }

        if (subscriptionWorkspace is not null &&
            domainWorkspace is not null &&
            !subscriptionWorkspace.Key.Equals(domainWorkspace.Key, StringComparison.OrdinalIgnoreCase))
        {
            throw new TargetWorkspaceResolutionException(
                TargetWorkspaceResolutionError.ConflictingRoutes,
                "Subscription and domain resolve to different configured workspaces.");
        }

        var workspace = subscriptionWorkspace ?? domainWorkspace;
        if (workspace is null)
        {
            throw new TargetWorkspaceResolutionException(
                TargetWorkspaceResolutionError.NoRoute,
                "The target subscription and domain do not match a configured workspace.");
        }

        var matchedBy = subscriptionWorkspace is not null && domainWorkspace is not null
            ? TargetWorkspaceMatchedBy.SubscriptionAndDomain
            : subscriptionWorkspace is not null
                ? TargetWorkspaceMatchedBy.Subscription
                : TargetWorkspaceMatchedBy.Domain;
        return new TargetWorkspaceResolution(workspace, matchedBy);
    }
}

public sealed record TargetWorkspaceResolution(
    CrossTenantLogAnalyticsWorkspaceOptions Workspace,
    TargetWorkspaceMatchedBy MatchedBy);

public enum TargetWorkspaceMatchedBy
{
    Subscription,
    Domain,
    SubscriptionAndDomain
}

public enum TargetWorkspaceResolutionError
{
    NoRoute,
    ConflictingRoutes
}

public sealed class TargetWorkspaceResolutionException(
    TargetWorkspaceResolutionError error,
    string message) : InvalidOperationException(message)
{
    public TargetWorkspaceResolutionError Error { get; } = error;
}
