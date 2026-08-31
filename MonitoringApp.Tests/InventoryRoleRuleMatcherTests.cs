using System.Text.Json;

namespace MonitoringApp.Tests;

public sealed class InventoryRoleRuleMatcherTests
{
    [Theory]
    [InlineData("DCDiag")]
    [InlineData("Replication")]
    public void AssignsDomainControllerRoleForMatchingQueryResult(string queryResultType)
    {
        var alert = CreateAlert(queryResultType);
        var rule = CreateRule(queryResultType, "domaincontrollers");

        var role = InventoryRoleRuleMatcher.FindRole(alert, [rule]);

        Assert.Equal("domaincontrollers", role);
    }

    [Fact]
    public void DoesNotAssignRoleForUnrelatedQueryResult()
    {
        var role = InventoryRoleRuleMatcher.FindRole(
            CreateAlert("Heartbeat"),
            [CreateRule("DCDiag", "domaincontrollers")]);

        Assert.Null(role);
    }

    [Fact]
    public void UsesTheHighestPriorityEnabledMatchingRule()
    {
        var rules = new[]
        {
            CreateRule("DCDiag", "later", priority: 20),
            CreateRule("DCDiag", "disabled", priority: 1, enabled: false),
            CreateRule("DCDiag", "first", priority: 10)
        };

        var role = InventoryRoleRuleMatcher.FindRole(CreateAlert("DCDiag"), rules);

        Assert.Equal("first", role);
    }

    private static AlertRule CreateRule(
        string queryResultType,
        string role,
        int priority = 10,
        bool enabled = true) => new()
    {
        Enabled = enabled,
        Priority = priority,
        RuleType = AlertRuleTypes.InventoryRoleAssignment,
        QueryResultType = queryResultType,
        InventoryRole = role
    };

    private static AlertRecord CreateAlert(string queryResultType) => new(
        Guid.NewGuid(),
        new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero),
        "alert-id",
        "Test alert",
        "Sev1",
        string.Empty,
        "Log",
        "Fired",
        "dc01.contoso.test",
        "rg-test",
        "sub-test",
        null,
        string.Empty,
        string.Empty,
        string.Empty,
        JsonSerializer.Serialize(new { queryResult = new { type = queryResultType } }));
}
