namespace MonitoringApp.Tests;

public sealed class CriticalAlertLogbookTests
{
    private static readonly CriticalAlertLogbookTestCases TestCases =
        TestCaseLoader.Load<CriticalAlertLogbookTestCases>("critical-alert-logbook.json");

    public static TheoryData<CriticalAlertLogbookCase> Cases =>
        new(TestCases.Cases);

    [Theory]
    [MemberData(nameof(Cases))]
    public void CreatesExpectedEntry(CriticalAlertLogbookCase testCase)
    {
        var alert = TestAlertFactory.FromFixture(TestCases.Alert, testCase.Condition);

        var entry = CriticalAlertLogbook.CreateEntry(alert, testCase.IsCritical, TestCases.CreatedAt);

        if (!testCase.ExpectEntry)
        {
            Assert.Null(entry);
            return;
        }

        Assert.NotNull(entry);
        Assert.Equal(TestCases.CreatedAt, entry.CreatedAt);
        Assert.Equal("System", entry.User);
        Assert.StartsWith(testCase.ExpectedPrefix, entry.Comment);
        foreach (var expected in testCase.ExpectedContains)
        {
            Assert.Contains(expected, entry.Comment);
        }
    }
}
