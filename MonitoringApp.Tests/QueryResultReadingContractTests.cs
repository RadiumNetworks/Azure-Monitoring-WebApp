namespace MonitoringApp.Tests;

public sealed class QueryResultReadingContractTests
{
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"queryResult":{"type":"DCPort"}}""")]
    [InlineData("""{"queryResult":{"type":"DCPort","columns":{},"rows":[]}}""")]
    [InlineData("""{"queryResult":{"type":"DCPort","columns":[],"rows":null}}""")]
    [InlineData("""{"queryResult":{"type":"DCPort","columns":[],"rows":{}}}""")]
    [InlineData("""{"queryResult":{"type":" ","columns":[],"rows":[]}}""")]
    public void MissingUnsupportedOrMalformedJsonUsesEachConsumersEmptyResult(string rawJson)
    {
        Assert.Null(Presenter.Parse(rawJson));
        Assert.Empty(AlertInventoryLearning.LearnComputerDomains(rawJson));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"queryResult":null}""")]
    [InlineData("""{"queryResult":[]}""")]
    [InlineData("""{"queryResult":{"type":"DCPort","columns":[null],"rows":[]}}""")]
    public void NonObjectEnvelopesAndColumnsKeepTheirExistingStructuralError(string rawJson)
    {
        Assert.Throws<InvalidOperationException>(() => Presenter.Parse(rawJson));
        Assert.Throws<InvalidOperationException>(() => AlertInventoryLearning.LearnComputerDomains(rawJson));
    }

    [Fact]
    public void DuplicateColumnNamesRemainExplicitErrors()
    {
        const string rawJson = """
            {"queryResult":{"type":"DCPort",
                "columns":[{"name":"RemoteSystem"},{"name":"remotesystem"}],"rows":[]}}
            """;

        Assert.Throws<ArgumentException>(() => Presenter.Parse(rawJson));
        Assert.Throws<ArgumentException>(() => AlertInventoryLearning.LearnComputerDomains(rawJson));
    }

    [Fact]
    public void UnsupportedTypesAreRejectedBeforeReadingDuplicateColumns()
    {
        const string rawJson = """
            {"queryResult":{"type":"Unknown",
                "columns":[{"name":"RemoteSystem"},{"name":"remotesystem"}],"rows":[]}}
            """;

        Assert.Null(Presenter.Parse(rawJson));
        Assert.Empty(AlertInventoryLearning.LearnComputerDomains(rawJson));
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("true", "True")]
    [InlineData("null", "")]
    [InlineData("""{"host":"DC-01"}""", """{"host":"DC-01"}""")]
    [InlineData("[1,2]", "[1,2]")]
    public void PresentationConvertsValuesToTextWhileLearningRequiresStrings(string valueJson, string expectedText)
    {
        var rawJson = $$$"""
            {"queryResult":{"type":"dcport",
                "columns":[{"name":"remotesystem"},{"name":"REMOTEDOMAIN"},
                           {"name":"LocalSystem"},{"name":"LocalDomain"}],
                "rows":[[{{{valueJson}}},"remote.test","LOCAL-DC","local.test"]]}}
            """;

        var presentation = Assert.IsType<QueryResultPresentation>(Presenter.Parse(rawJson));
        Assert.Equal(expectedText, Assert.Single(presentation.Rows).Title);
        Assert.Equal(new LearnedComputerDomain("LOCAL-DC", "local.test"),
            Assert.Single(AlertInventoryLearning.LearnComputerDomains(rawJson)));
    }

    [Fact]
    public void ReadersKeepColumnPositionsIgnoreNonArrayRowsAndHandleShortRows()
    {
        const string rawJson = """
            {"queryResult":{"type":"DCPort",
                "columns":[{},{"name":"remotesystem"},{"name":"REMOTEDOMAIN"},{"name":"RemoteSite"}],
                "rows":[null,{},"ignored",[],[0,"REMOTE-DC","remote.test",17],[0,"SHORT"]]}}
            """;

        var presentation = Assert.IsType<QueryResultPresentation>(Presenter.Parse(rawJson));
        Assert.Equal(["", "REMOTE-DC", "SHORT"], presentation.Rows.Select(row => row.Title));
        Assert.Equal(new LearnedComputerDomain("REMOTE-DC", "remote.test"),
            Assert.Single(AlertInventoryLearning.LearnComputerDomains(rawJson)));
        Assert.Equal("17", Assert.Single(presentation.Rows[1].Metadata).Value);
    }

    [Fact]
    public void ZeroRowCountRemainsALearningPolicyRatherThanAStructuralFilter()
    {
        const string rawJson = """
            {"queryResult":{"type":"DCPort","rowCount":0,
                "columns":[{"name":"RemoteSystem"},{"name":"RemoteDomain"}],
                "rows":[["REMOTE-DC","remote.test"]]}}
            """;

        Assert.Single(Assert.IsType<QueryResultPresentation>(Presenter.Parse(rawJson)).Rows);
        Assert.Empty(AlertInventoryLearning.LearnComputerDomains(rawJson));
    }
}
