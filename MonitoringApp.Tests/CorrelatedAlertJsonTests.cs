using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class CorrelatedAlertJsonTests
{
    private static readonly CorrelatedAlertTestCases Cases =
        TestCaseLoader.Load<CorrelatedAlertTestCases>("correlated-alert-presentations.json");
    private static readonly IReadOnlyDictionary<string, JsonObject> Payloads =
        TestCaseLoader.Load<JsonArray>("correlated-alerts.json")
            .Select(node => node?.AsObject() ?? throw new InvalidOperationException("Alert payload must be an object."))
            .ToDictionary(TestAlertFactory.GetAlertId, StringComparer.Ordinal);
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
    public static IEnumerable<object[]> AlertCaseIndexes => Indexes(Cases.Alerts.Count);

    [Theory]
    [MemberData(nameof(AlertCaseIndexes))]
    public void CorrelatedAlertIdentityAndPresentationMatchJsonCases(int caseIndex)
    {
        var testCase = Cases.Alerts[caseIndex];
        var payload = Payloads[testCase.AlertId];
        var alert = TestAlertFactory.FromCommonPayload(payload);
        var identity = Presenter.ResolveIdentity(alert);
        var presentation = Assert.IsType<QueryResultPresentation>(Presenter.Parse(alert.RawJson));

        Assert.Equal(testCase.TargetName, identity.TargetName);
        Assert.Equal(testCase.SiteName, identity.SiteName);
        Assert.Equal(testCase.CollapseRows, presentation.CollapseRows);
        Assert.Equal(testCase.Summary, presentation.Summary.Select(badge => badge.Text));
        Assert.Equal(testCase.RowTitles, presentation.Rows.Select(row => row.Title));
        AssertContains(testCase.Metadata, presentation.Rows.SelectMany(row => row.Metadata).Select(Format));
        AssertContains(testCase.Alerts, presentation.Rows.SelectMany(row => row.Alerts).Select(Format));
        AssertContains(testCase.Facts, presentation.Rows.SelectMany(row => row.Facts).Select(Format));
    }

    private static string Format(QueryResultValue value) => $"{value.Label}={value.Value}";
    private static string Format(QueryResultItem item) => $"{item.Label}={item.Value}";

    private static void AssertContains(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var actualValues = actual.ToArray();
        foreach (var value in expected)
        {
            Assert.Contains(value, actualValues);
        }
    }

    private static IEnumerable<object[]> Indexes(int count) =>
        Enumerable.Range(0, count).Select(index => new object[] { index });
}