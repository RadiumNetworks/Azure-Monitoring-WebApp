namespace MonitoringApp.Tests;

public sealed class AlertLifecycleJsonTests
{
    private static readonly AlertLifecycleTestCases Cases =
        TestCaseLoader.Load<AlertLifecycleTestCases>("alert-lifecycle.json");
    public static IEnumerable<object[]> ActiveCaseIndexes => Indexes(Cases.ActiveCases.Count);

    [Theory]
    [MemberData(nameof(ActiveCaseIndexes))]
    public void ActiveAlertsMatchJsonLifecycleCases(int caseIndex)
    {
        var testCase = Cases.ActiveCases[caseIndex];
        var active = AlertLifecycle.GetActiveAlerts(Create(testCase.Alerts));
        Assert.Equal(testCase.ExpectedKeys, active.Select(Key));
    }

    [Fact]
    public void NavigationHierarchyCountsLogicalOpenAlertsCumulatively()
    {
        var active = AlertLifecycle.GetActiveAlerts(Create(Cases.Navigation.Alerts));
        Assert.Equal(Cases.Navigation.ExpectedActiveCount, active.Count);
        foreach (var expected in Cases.Navigation.Counts)
        {
            Assert.Equal(expected.Expected, active.Count(alert =>
                alert.SubscriptionId == expected.SubscriptionId &&
                (expected.ResourceGroup is null || alert.ResourceGroup == expected.ResourceGroup) &&
                (expected.Target is null || alert.TargetName == expected.Target)));
        }
    }

    [Fact]
    public void NavigationIncludesOnlyJsonBranchesWithinRecentWindow()
    {
        var testCase = Cases.RecentNavigation;
        var alerts = Create(testCase.Alerts);
        var navigation = AlertNavigation.Build(alerts, AlertLifecycle.GetActiveAlerts(alerts), testCase.Cutoff);

        Assert.Equal(testCase.ExpectedSubscriptions, navigation.Select(node => node.Name));
        foreach (var branchCase in testCase.Branches)
        {
            var branch = navigation.Single(node => node.Name == branchCase.SubscriptionId);
            Assert.Equal(branchCase.Count, branch.Count);
            Assert.Equal(branchCase.HistoryCount, branch.HistoryCount);
            Assert.Equal(branchCase.ResourceGroupHistoryCount, Assert.Single(branch.ResourceGroups).HistoryCount);
            Assert.Equal(branchCase.TargetHistoryCount, Assert.Single(Assert.Single(branch.ResourceGroups).Targets).HistoryCount);
        }
        foreach (var excluded in testCase.ExcludedSubscriptions)
        {
            Assert.DoesNotContain(navigation, node => node.Name == excluded);
        }
    }

    private static AlertRecord[] Create(IEnumerable<AlertEventCase> cases) =>
        cases.Select(testCase => TestAlertFactory.FromEvent(testCase, Cases.Defaults)).ToArray();

    private static string Key(AlertRecord alert) =>
        $"{alert.AlertId}|{alert.TargetName}|{alert.MonitorCondition}|{alert.ReceivedAt:O}";

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}