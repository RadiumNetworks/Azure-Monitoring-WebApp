namespace MonitoringApp.Tests;

public sealed class AlertGraphFilterJsonTests
{
    private static readonly AlertGraphFilterTestCases TestCases =
        TestCaseLoader.Load<AlertGraphFilterTestCases>("alert-graph-filter.json");

    public static TheoryData<AlertGraphFilterCase> Cases => new(TestCases.Cases);

    [Theory]
    [MemberData(nameof(Cases))]
    public void FiltersConfiguredGraphFields(AlertGraphFilterCase testCase)
    {
        var alerts = TestCases.Alerts.Select(CreateRecord).ToArray();

        var result = AlertGraphFilter.Apply(alerts, testCase.Filter);

        Assert.Equal(testCase.ExpectedAlertIds, result.Select(alert => alert.AlertId));
    }

    private static AlertGraphRecord CreateRecord(GraphRecordCase source) => new(
        Guid.NewGuid(), source.ReceivedAt, source.AlertId, source.MonitorCondition, source.Category,
        source.AlertName, source.SubscriptionId, source.ResourceGroup, source.Target, source.Site,
        source.Domain, source.Role);
}
