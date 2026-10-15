namespace MonitoringApp.Tests;

public sealed class TargetWorkspaceResolverTests
{
    private static readonly CrossTenantLogAnalyticsWorkspaceOptions First =
        CrossTenantLogAnalyticsOptionsTests.Workspace(
            "first",
            "11111111-1111-1111-1111-111111111111",
            "first.example");
    private static readonly CrossTenantLogAnalyticsWorkspaceOptions Second =
        CrossTenantLogAnalyticsOptionsTests.Workspace(
            "second",
            "22222222-2222-2222-2222-222222222222",
            "second.example");
    private static readonly TargetWorkspaceResolver Resolver =
        new(CrossTenantLogAnalyticsOptionsTests.CreateValid([First, Second]));

    [Fact]
    public void ResolvesBySubscription()
    {
        var result = Resolver.Resolve("11111111-1111-1111-1111-111111111111", null);

        Assert.Equal("first", result.Workspace.Key);
        Assert.Equal(TargetWorkspaceMatchedBy.Subscription, result.MatchedBy);
    }

    [Fact]
    public void ResolvesNormalizedDomainAlias()
    {
        var result = Resolver.Resolve(null, " SECOND.EXAMPLE. ");

        Assert.Equal("second", result.Workspace.Key);
        Assert.Equal(TargetWorkspaceMatchedBy.Domain, result.MatchedBy);
    }

    [Fact]
    public void ConfirmsMatchingSubscriptionAndDomain()
    {
        var result = Resolver.Resolve(
            "22222222-2222-2222-2222-222222222222",
            "second.example");

        Assert.Equal("second", result.Workspace.Key);
        Assert.Equal(TargetWorkspaceMatchedBy.SubscriptionAndDomain, result.MatchedBy);
    }

    [Fact]
    public void RejectsConflictingSubscriptionAndDomain()
    {
        var exception = Assert.Throws<TargetWorkspaceResolutionException>(() =>
            Resolver.Resolve(
                "11111111-1111-1111-1111-111111111111",
                "second.example"));

        Assert.Equal(TargetWorkspaceResolutionError.ConflictingRoutes, exception.Error);
    }

    [Fact]
    public void RejectsTargetWithoutRoute()
    {
        var exception = Assert.Throws<TargetWorkspaceResolutionException>(() =>
            Resolver.Resolve(null, "unknown.example"));

        Assert.Equal(TargetWorkspaceResolutionError.NoRoute, exception.Error);
    }
}
