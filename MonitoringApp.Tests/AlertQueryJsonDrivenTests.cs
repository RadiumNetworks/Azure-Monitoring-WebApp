namespace MonitoringApp.Tests;

public sealed class AlertQueryJsonDrivenTests
{
    private static readonly AlertQueryTestCases Cases = TestCaseLoader.Load<AlertQueryTestCases>("alert-query.json");
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
    public static IEnumerable<object[]> IdentityCaseIndexes => Indexes(Cases.Identities.Count);

    [Fact]
    public void ActiveQueryAppliesLifecycleBeforeTimeWindow()
    {
        var result = AlertQuery.GetActiveSince(Create(Cases.ActiveQuery.Alerts), Cases.ActiveQuery.Since);
        Assert.Equal(Cases.ActiveQuery.ExpectedAlertIds, result.Select(alert => alert.AlertId));
    }

    [Fact]
    public void EventQueryReturnsEveryConditionWithinWindowNewestFirst()
    {
        var result = AlertQuery.GetEventsSince(Create(Cases.EventQuery.Alerts), Cases.EventQuery.Since);
        Assert.Equal(Cases.EventQuery.ExpectedAlertIds, result.Select(alert => alert.AlertId));
    }

    [Fact]
    public void QueryItemUsesJsonTargetAndNullableSearchLink()
    {
        var item = AlertQueryItem.FromAlert(TestAlertFactory.WithPayload(Cases.QueryItem.Payload, Cases.Defaults));
        Assert.Equal(Cases.QueryItem.ExpectedTarget, item.Target);
        Assert.Equal(Cases.QueryItem.ExpectedSearchResultLink, item.SearchResultLink);
    }

    [Theory]
    [MemberData(nameof(IdentityCaseIndexes))]
    public void IdentityCasesResolveFromJsonDefinitions(int caseIndex)
    {
        var testCase = Cases.Identities[caseIndex];
        var alert = TestAlertFactory.WithPayload(testCase.Payload, Cases.Defaults);
        var identity = Presenter.ResolveIdentity(alert);
        var resolved = alert with { DisplayIdentity = identity };

        Assert.Equal(testCase.ExpectedTarget, resolved.TargetName);
        Assert.Equal(testCase.ExpectedSite, resolved.SiteName);
        Assert.Equal(testCase.ExpectedDisplayName, resolved.TargetDisplayName);
    }

    [Fact]
    public void SearchQueryIsDecodedAndPreservesLineBreaks()
    {
        var alert = TestAlertFactory.WithPayload(Cases.SearchQuery.Payload, Cases.Defaults);
        Assert.Equal(Cases.SearchQuery.Expected, alert.SearchQuery);
    }

    private static AlertRecord[] Create(IEnumerable<AlertEventCase> cases) =>
        cases.Select(testCase => TestAlertFactory.FromEvent(testCase, Cases.Defaults)).ToArray();

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}