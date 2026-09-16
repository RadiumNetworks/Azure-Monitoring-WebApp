namespace MonitoringApp.Tests;

public sealed class AlertInventoryLearningTests
{
    [Fact]
    public void LearnsLocalAndRemoteDomainsFromDcPortRows()
    {
        const string rawJson = """
            {
              "queryResult": {
                "type": "DCPort",
                "columns": [
                  { "name": "RemoteSystem", "type": "string" },
                  { "name": "RemoteDomain", "type": "string" },
                  { "name": "LocalDomain", "type": "string" },
                  { "name": "LocalSystem", "type": "string" }
                ],
                "rows": [
                  ["REMOTE-DC", "remote.example", "local.example", "LOCAL-DC"]
                ]
              }
            }
            """;

        var learned = AlertInventoryLearning.LearnComputerDomains(rawJson);

        Assert.Contains(new LearnedComputerDomain("LOCAL-DC", "local.example"), learned);
        Assert.Contains(new LearnedComputerDomain("REMOTE-DC", "remote.example"), learned);
    }

    [Fact]
    public void IgnoresIncompleteAndConflictingDomainFacts()
    {
        const string rawJson = """
            {
              "queryResult": {
                "type": "DCPort",
                "columns": [
                  { "name": "LocalSystem" },
                  { "name": "LocalDomain" },
                  { "name": "RemoteSystem" },
                  { "name": "RemoteDomain" }
                ],
                "rows": [
                  ["DC-01", "first.example", "DC-02", ""],
                  ["DC-01", "second.example", "DC-03", "remote.example"]
                ]
              }
            }
            """;

        var learned = AlertInventoryLearning.LearnComputerDomains(rawJson);

        Assert.DoesNotContain(learned, fact => fact.Computer == "DC-01");
        Assert.DoesNotContain(learned, fact => fact.Computer == "DC-02");
        Assert.Contains(new LearnedComputerDomain("DC-03", "remote.example"), learned);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"queryResult\":{\"type\":\"Heartbeat\",\"columns\":[],\"rows\":[]}}")]
    public void IgnoresUnsupportedPayloads(string rawJson)
    {
        Assert.Empty(AlertInventoryLearning.LearnComputerDomains(rawJson));
    }
}