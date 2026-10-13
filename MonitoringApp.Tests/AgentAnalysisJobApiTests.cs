using System.Security.Claims;

namespace MonitoringApp.Tests;

public sealed class AgentAnalysisJobApiTests
{
    [Fact]
    public void ValidationRejectsUnboundedOrUnauthorizedScopeInputs()
    {
        var request = new AgentAnalysisJobStartRequest(
            [Guid.Empty],
            nameof(IncidentAnalysisScopeType.GraphSubtree),
            "subscription-a",
            AgentAnalysisJobApi.MaxSystemHistoryDays + 1,
            AgentAnalysisJobApi.MaxContextHours + 1);

        var errors = AgentAnalysisJobApi.Validate(request);

        Assert.Contains(nameof(request.PrimaryEventIds), errors.Keys);
        Assert.Contains(nameof(request.ScopeType), errors.Keys);
        Assert.Contains(nameof(request.SystemHistoryDays), errors.Keys);
        Assert.Contains(nameof(request.ContextHours), errors.Keys);
    }

    [Fact]
    public void AccessAllowsOwnerAndAdminButNotAnotherOperator()
    {
        var job = new AgentAnalysisJob { CreatedBy = "owner" };
        var owner = CreatePrincipal("OWNER", SqlAuthenticationRoles.Operator);
        var other = CreatePrincipal("other", SqlAuthenticationRoles.Operator);
        var admin = CreatePrincipal("admin", SqlAuthenticationRoles.Admin);

        Assert.True(AgentAnalysisJobApi.CanAccess(job, owner, openAuthentication: false));
        Assert.False(AgentAnalysisJobApi.CanAccess(job, other, openAuthentication: false));
        Assert.True(AgentAnalysisJobApi.CanAccess(job, admin, openAuthentication: false));
    }

    [Fact]
    public void ScopePolicyRejectsTenantOrSubscriptionOutsideConfiguredBoundary()
    {
        var policy = new AgentAnalysisScopePolicy(new AgentAnalysisAccessOptions
        {
            AllowedTenantIds = ["tenant-a"],
            AllowedSubscriptionIds = ["subscription-a"]
        });
        var wrongTenant = CreatePrincipal("operator", SqlAuthenticationRoles.Operator, "tenant-b");
        var allowedTenant = CreatePrincipal("operator", SqlAuthenticationRoles.Operator, "tenant-a");

        Assert.Throws<AgentAnalysisScopeDeniedException>(() =>
            policy.EnsureAllowed(wrongTenant, openAuthentication: false, CreateSnapshot("subscription-a")));
        Assert.Throws<AgentAnalysisScopeDeniedException>(() =>
            policy.EnsureAllowed(allowedTenant, openAuthentication: false, CreateSnapshot("subscription-b")));
        policy.EnsureAllowed(allowedTenant, openAuthentication: false, CreateSnapshot("subscription-a"));
    }

    [Fact]
    public void TargetScopeRejectsSameNamedTargetsAcrossSubscriptions()
    {
        var policy = new AgentAnalysisScopePolicy(new AgentAnalysisAccessOptions());
        var snapshot = CreateSnapshot("subscription-a") with
        {
            PrimaryEvidence =
            [
                CreateEvidence("subscription-a", "target-a"),
                CreateEvidence("subscription-b", "target-a")
            ]
        };

        Assert.Throws<AgentAnalysisScopeDeniedException>(() =>
            policy.EnsureAllowed(
                CreatePrincipal("operator", SqlAuthenticationRoles.Operator),
                openAuthentication: false,
                snapshot));
    }

    private static ClaimsPrincipal CreatePrincipal(string name, string role, string? tenantId = null) => new(
        new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, name),
                new Claim(ClaimTypes.Role, role)
            }.Concat(tenantId is null ? [] : [new Claim("tid", tenantId)]),
            "Test"));

    private static IncidentAnalysisSnapshot CreateSnapshot(string subscriptionId) => new(
        DateTimeOffset.UtcNow,
        new IncidentAnalysisScope(IncidentAnalysisScopeType.Target, "target-a"),
        DateTimeOffset.UtcNow.AddHours(-1),
        DateTimeOffset.UtcNow,
        100,
        120_000,
        [CreateEvidence(subscriptionId, "target-a")],
        [],
        [],
        [],
        new IncidentGraphContext(
            AlertGraphLayer.Subscription,
            AlertGraphLayer.Site,
            AlertGraphLayer.Target,
            "Selected target",
            []),
        [],
        [],
        [],
        [],
        [],
        []);

    private static IncidentAlertEvidence CreateEvidence(string subscriptionId, string target) => new(
        Guid.NewGuid(),
        "alert-a",
        IncidentEvidenceTier.Primary,
        "Selected by operator",
        TimeSpan.Zero,
        [],
        [],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "Alert",
        "Sev1",
        "Metric",
        "Fired",
        subscriptionId,
        "resource-group-a",
        target,
        "site-a",
        "domain-a",
        "server",
        "Description",
        new IncidentRuleDecision(
            IncidentRuleDecisionType.None,
            IncidentAssessmentOrigin.Telemetry,
            null,
            string.Empty,
            string.Empty,
            true,
            false,
            "Visible",
            false),
        null);
}
