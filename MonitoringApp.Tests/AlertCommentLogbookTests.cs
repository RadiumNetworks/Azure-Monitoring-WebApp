namespace MonitoringApp.Tests;

public sealed class AlertCommentLogbookTests
{
    private static readonly AlertCommentLogbookTestCases TestCases =
        TestCaseLoader.Load<AlertCommentLogbookTestCases>("alert-comment-logbook.json");

    public static TheoryData<AlertCommentLogbookCase> Cases =>
        new(TestCases.Cases);

    [Theory]
    [MemberData(nameof(Cases))]
    public void CreatesExpectedEntryOrValidationError(AlertCommentLogbookCase testCase)
    {
        var alert = TestAlertFactory.FromFixture(TestCases.Alert);
        if (!string.IsNullOrEmpty(testCase.ExpectedError))
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                AlertCommentLogbook.CreateEntry(alert, testCase.User, testCase.Comment, TestCases.CreatedAt));
            Assert.Contains(testCase.ExpectedError, exception.Message);
            return;
        }

        var entry = AlertCommentLogbook.CreateEntry(alert, testCase.User, testCase.Comment, TestCases.CreatedAt);
        if (!testCase.ExpectEntry)
        {
            Assert.Null(entry);
            return;
        }

        Assert.NotNull(entry);
        Assert.Equal(TestCases.CreatedAt, entry.CreatedAt);
        Assert.Equal(testCase.ExpectedUser, entry.User);
        foreach (var expected in testCase.ExpectedContains)
        {
            Assert.Contains(expected, entry.Comment);
        }
    }
}
