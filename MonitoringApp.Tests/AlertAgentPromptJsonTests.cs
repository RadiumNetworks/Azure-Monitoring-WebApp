using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class AlertAgentPromptJsonTests
{
    private static readonly AlertPromptTestCases Cases =
        TestCaseLoader.Load<AlertPromptTestCases>("alert-agent-prompt.json");
    private static readonly QueryResultPresenter Presenter =
        new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
    private static readonly IReadOnlyDictionary<string, JsonObject> CorrelatedPayloads =
        TestCaseLoader.Load<JsonArray>("correlated-alerts.json")
            .Select(node => node?.AsObject() ?? throw new InvalidOperationException("Alert payload must be an object."))
            .ToDictionary(TestAlertFactory.GetAlertId, StringComparer.Ordinal);

    [Fact]
    public void DefaultPromptIncludesConfiguredContentOnly()
    {
        var alert = TestAlertFactory.FromPrompt(Cases.DefaultAlert);
        var prompt = AlertAgentPrompt.Build([alert], Cases.GeneratedAt, new AlertAgentPromptOptions(), Presenter)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        AssertContains(Cases.DefaultContains, prompt);
        AssertExcludes(Cases.DefaultExcludes, prompt);
    }

    [Fact]
    public void OptionalFieldsCanAllBeExcluded()
    {
        var prompt = AlertAgentPrompt.Build(
            [TestAlertFactory.FromPrompt(Cases.DefaultAlert)], Cases.GeneratedAt, Cases.ExcludedOptions);
        AssertExcludes(Cases.ExcludedContent, prompt);
    }

    [Fact]
    public void PromptIncludesCorrelatedDomainControllerEvidenceAndReferences()
    {
        var alerts = Cases.DiagnosticAlertIds.Select(id => TestAlertFactory.FromCommonPayload(CorrelatedPayloads[id]));
        var prompt = AlertAgentPrompt.Build(alerts, Cases.GeneratedAt, new AlertAgentPromptOptions(), Presenter);
        AssertContains(Cases.DiagnosticContains, prompt);
    }

    private static void AssertContains(IEnumerable<string> expected, string actual)
    {
        foreach (var value in expected)
        {
            Assert.Contains(value, actual);
        }
    }

    private static void AssertExcludes(IEnumerable<string> excluded, string actual)
    {
        foreach (var value in excluded)
        {
            Assert.DoesNotContain(value, actual);
        }
    }
}