using PlaywrightDemo.Parsing;

namespace PlaywrightDemo.Tests;

[TestFixture]
public sealed class ParsingTests
{
    [Test]
    public void ParsesResultAndChartSummaries()
    {
        Assert.That(AlertConsoleParsers.ParseResultSummary("Showing 6 of 11 alerts in this view"),
            Is.EqualTo(new Models.ResultSummary(6, 11)));
        Assert.That(AlertConsoleParsers.ParseChartDescription(
                "Alerts received per hour over the last 48 hours. 11 total, with a maximum of 4 in one hour."),
            Is.EqualTo(new Models.ChartSummary(11, 4)));
    }

    [Test]
    public void ParsesHierarchyLabelsAndUtcTooltips()
    {
        Assert.That(AlertConsoleParsers.ParseNavigationNode("Production Subscription 12"),
            Is.EqualTo(new Models.NavigationNode("Production Subscription", 12)));
        Assert.That(AlertConsoleParsers.ParseUtcTooltip("2026-08-21T11:41:25.0000000+00:00 UTC"),
            Is.EqualTo(new DateTimeOffset(2026, 8, 21, 11, 41, 25, TimeSpan.Zero)));
    }

    [Test]
    public void ParsesCommonAlertSchemaAndExtractsTargetName()
    {
        var payload = AlertConsoleParsers.ParseCommonAlertPayload("""
            {
              "schemaId": "azureMonitorCommonAlertSchema",
              "data": {
                "essentials": {
                  "monitorCondition": "Fired",
                                    "configurationItems": ["/subscriptions/sub/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/log-workspace"]
                                },
                                "alertContext": {
                                    "condition": {
                                        "allOf": [
                                            {
                                                "dimensions": [
                                                    { "name": "Computer", "value": "web-01" }
                                                ]
                                            }
                                        ]
                                    }
                }
              }
            }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(payload["data"]?["essentials"]?["monitorCondition"]?.GetValue<string>(), Is.EqualTo("Fired"));
            Assert.That(AlertConsoleParsers.TargetNameFromPayload(payload), Is.EqualTo("web-01"));
        });
    }

    [Test]
    public void RejectsUnexpectedTextAndNonObjectPayloads()
    {
        Assert.Throws<FormatException>(() => AlertConsoleParsers.ParseResultSummary("No summary"));
        Assert.Throws<FormatException>(() => AlertConsoleParsers.ParseCommonAlertPayload("[1, 2, 3]"));
    }
}