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
        var recentAlerts = alerts.Where(alert => alert.ReceivedAt >= testCase.Cutoff).ToArray();
        var inventory = recentAlerts.Select((alert, index) => new ComputerInventoryEntry
        {
            SubscriptionId = alert.SubscriptionId,
            Computer = alert.TargetName,
            Domain = index == 0 ? "contoso.example" : "fabrikam.example",
            Role = index == 0 ? "Domain Controller" : "Member Server",
            Site = index == 0 ? "Berlin" : "Hamburg"
        }).Append(new ComputerInventoryEntry
        {
            SubscriptionId = "inventory-subscription",
            Computer = "KNOWN-WITHOUT-ALERT",
            Domain = "contoso.example",
            Role = "Member Server",
            Site = "Munich"
        }).ToArray();

        var navigation = AlertNavigation.Build(alerts, AlertLifecycle.GetActiveAlerts(alerts), inventory, testCase.Cutoff);

        Assert.Equal(recentAlerts.Length, navigation.Sum(node => node.HistoryCount));
        var firstTarget = Assert.Single(navigation[0].Roles.Single(role => role.Name == "Domain Controller").Targets);
        Assert.Equal(recentAlerts[0].TargetName, firstTarget.Name);
        Assert.Equal("Berlin", firstTarget.Site);
        Assert.Equal($"{recentAlerts[0].TargetName} (Berlin)", firstTarget.DisplayName);
        var inventoryOnlyTarget = navigation
            .SelectMany(domain => domain.Roles)
            .SelectMany(role => role.Targets)
            .Single(target => target.Name == "KNOWN-WITHOUT-ALERT");
        Assert.Equal("Munich", inventoryOnlyTarget.Site);
        Assert.Equal(0, inventoryOnlyTarget.Count);
        Assert.Equal(0, inventoryOnlyTarget.HistoryCount);
        Assert.DoesNotContain(testCase.ExcludedSubscriptions, excluded =>
            navigation.SelectMany(domain => domain.Roles).SelectMany(role => role.Targets).Any(target =>
                alerts.Any(alert => alert.TargetName == target.Name && alert.SubscriptionId == excluded)));
    }

    [Fact]
    public void NavigationTreatsDomainAndTargetAsCaseInsensitiveSystemIdentity()
    {
        var olderAlert = TestAlertFactory.FromEvent(Cases.RecentNavigation.Alerts[0], Cases.Defaults) with
        {
            ReceivedAt = Cases.RecentNavigation.Cutoff.AddMinutes(1),
            SubscriptionId = "sub-a"
        };
        var latestAlert = olderAlert with
        {
            Id = Guid.NewGuid(),
            AlertId = Guid.NewGuid().ToString(),
            ReceivedAt = olderAlert.ReceivedAt.AddMinutes(1),
            SubscriptionId = "sub-b",
            Target = olderAlert.Target.ToLowerInvariant()
        };
        var first = new ComputerInventoryEntry
        {
            SubscriptionId = "sub-a",
            Computer = olderAlert.TargetName,
            Domain = "Example.Test",
            Role = "Server"
        };
        var second = new ComputerInventoryEntry
        {
            SubscriptionId = "sub-b",
            Computer = latestAlert.TargetName,
            Domain = "example.test",
            Role = "Server"
        };

        var navigation = AlertNavigation.Build(
            [olderAlert, latestAlert],
            [olderAlert, latestAlert],
            [first, second],
            Cases.RecentNavigation.Cutoff);

        var target = Assert.Single(Assert.Single(Assert.Single(navigation).Roles).Targets);
        Assert.Equal(2, target.Count);
        Assert.Equal(2, target.HistoryCount);
    }

    [Fact]
    public void NavigationDoesNotCreateSystemsForAlertsMissingFromInventory()
    {
        var alert = TestAlertFactory.FromEvent(Cases.RecentNavigation.Alerts[0], Cases.Defaults) with
        {
            ReceivedAt = Cases.RecentNavigation.Cutoff.AddMinutes(1)
        };

        var navigation = AlertNavigation.Build(
            [alert],
            [alert],
            [],
            Cases.RecentNavigation.Cutoff);

        Assert.Empty(navigation);
    }

    private static AlertRecord[] Create(IEnumerable<AlertEventCase> cases) =>
        cases.Select(testCase => TestAlertFactory.FromEvent(testCase, Cases.Defaults)).ToArray();

    private static string Key(AlertRecord alert) =>
        $"{alert.AlertId}|{alert.TargetName}|{alert.MonitorCondition}|{alert.ReceivedAt:O}";

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}