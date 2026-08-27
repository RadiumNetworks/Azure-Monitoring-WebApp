using PlaywrightDemo.Models;
using PlaywrightDemo.Parsing;
using PlaywrightDemo.TestCases;

namespace PlaywrightDemo.Tests;

[TestFixture]
public sealed class ParsingTests
{
    private static readonly ParsingTestCases Cases = TestCaseLoader.Load<ParsingTestCases>("parsing.json");

    [Test]
    public void ParsesResultAndChartSummaries()
    {
        foreach (var testCase in Cases.ResultSummaries)
        {
            Assert.That(AlertConsoleParsers.ParseResultSummary(testCase.Input),
                Is.EqualTo(new ResultSummary(testCase.Shown, testCase.Available)));
        }

        foreach (var testCase in Cases.ChartDescriptions)
        {
            Assert.That(AlertConsoleParsers.ParseChartDescription(testCase.Input),
                Is.EqualTo(new ChartSummary(testCase.Total, testCase.MaximumPerHour)));
        }
    }

    [Test]
    public void ParsesHierarchyLabelsAndUtcTooltips()
    {
        foreach (var testCase in Cases.NavigationNodes)
        {
            Assert.That(AlertConsoleParsers.ParseNavigationNode(testCase.Input),
                Is.EqualTo(new NavigationNode(testCase.Name, testCase.Count)));
        }

        foreach (var testCase in Cases.UtcTooltips)
        {
            Assert.That(AlertConsoleParsers.ParseUtcTooltip(testCase.Input), Is.EqualTo(testCase.Expected));
        }
    }

    [Test]
    public void ParsesCommonAlertSchemaAndExtractsTargetName()
    {
        foreach (var testCase in Cases.AlertPayloads)
        {
            var payload = AlertConsoleParsers.ParseCommonAlertPayload(testCase.Payload.ToJsonString());
            Assert.Multiple(() =>
            {
                Assert.That(payload["data"]?["essentials"]?["monitorCondition"]?.GetValue<string>(),
                    Is.EqualTo(testCase.MonitorCondition));
                Assert.That(AlertConsoleParsers.TargetNameFromPayload(payload), Is.EqualTo(testCase.TargetName));
            });
        }
    }

    [Test]
    public void RejectsUnexpectedTextAndNonObjectPayloads()
    {
        foreach (var testCase in Cases.InvalidCases)
        {
            Assert.That(() => ParseInvalidCase(testCase), Throws.TypeOf<FormatException>());
        }
    }

    private static void ParseInvalidCase(InvalidParserCase testCase)
    {
        switch (testCase.Parser)
        {
            case "result-summary":
                AlertConsoleParsers.ParseResultSummary(testCase.Input);
                break;
            case "common-alert":
                AlertConsoleParsers.ParseCommonAlertPayload(testCase.Input);
                break;
            default:
                throw new InvalidOperationException($"Unknown parser test case '{testCase.Parser}'.");
        }
    }
}