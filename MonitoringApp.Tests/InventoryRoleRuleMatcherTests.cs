namespace MonitoringApp.Tests;

public sealed class InventoryRoleRuleMatcherTests
{
    private static readonly InventoryRoleRuleMatcherTestCases TestCases =
        TestCaseLoader.Load<InventoryRoleRuleMatcherTestCases>("inventory-role-rule-matcher.json");

    public static TheoryData<InventoryRoleRuleMatcherCase> Cases =>
        new(TestCases.Cases);

    [Theory]
    [InlineData("domaincontrollers", "Domain Controller")]
    [InlineData(" DomainControllers ", "Domain Controller")]
    [InlineData("domain controller", "Domain Controller")]
    [InlineData("Domain Controller", "Domain Controller")]
    [InlineData("Member Server", "Member Server")]
    [InlineData(null, null)]
    public void NormalizesOnlyDomainControllerRoles(string? role, string? expected)
    {
        Assert.Equal(expected, InventoryRoleRuleMatcher.NormalizeRole(role));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FindsExpectedRole(InventoryRoleRuleMatcherCase testCase)
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["queryResult"] = new System.Text.Json.Nodes.JsonObject { ["type"] = testCase.QueryResultType }
        };
        var alert = TestAlertFactory.FromFixture(TestCases.Alert, payload: payload);

        var role = InventoryRoleRuleMatcher.FindRole(alert, testCase.Rules);

        Assert.Equal(testCase.ExpectedRole, role);
    }
}
