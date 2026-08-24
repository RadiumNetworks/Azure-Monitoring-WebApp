namespace MonitoringApp.Tests;

public sealed class AlertQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveQueryAppliesLifecycleBeforeTimeWindow()
    {
        var recentFired = CreateAlert("recent-open", "Fired", Now.AddMinutes(-5));
        var oldFired = CreateAlert("old-open", "Fired", Now.AddMinutes(-45));

        var result = AlertQuery.GetActiveSince([
            recentFired,
            oldFired,
            CreateAlert("recent-resolved", "Fired", Now.AddMinutes(-10)),
            CreateAlert("recent-resolved", "Resolved", Now.AddMinutes(-2)),
            CreateAlert("recent-manual", "Fired", Now.AddMinutes(-3), "Resolved by operator")
        ], Now.AddMinutes(-30));

        Assert.Same(recentFired, Assert.Single(result));
    }

    [Fact]
    public void EventQueryReturnsEveryConditionWithinWindowNewestFirst()
    {
        var fired = CreateAlert("alert-1", "Fired", Now.AddHours(-2));
        var resolved = CreateAlert("alert-1", "Resolved", Now.AddHours(-1));

        var result = AlertQuery.GetEventsSince([
            CreateAlert("old", "Fired", Now.AddHours(-25)),
            fired,
            resolved
        ], Now.AddHours(-24));

        Assert.Equal([resolved, fired], result);
    }

    [Fact]
    public void QueryItemUsesTargetNameAndNullableSearchLink()
    {
        var alert = CreateAlert("alert-1", "Fired", Now, rawJson: """
            { "data": { "essentials": { "configurationItems": ["DOMAIN\\servers\\web-01"] } } }
            """);

        var item = AlertQueryItem.FromAlert(alert);

        Assert.Equal("web-01", item.Target);
        Assert.Null(item.SearchResultLink);
    }

        [Fact]
        public void TargetNameUsesSupportedDimensionAndIgnoresOtherDimensions()
        {
                var alert = CreateAlert("alert-1", "Fired", Now, rawJson: """
                        {
                            "data": {
                                "essentials": {
                                    "configurationItems": ["/subscriptions/sub/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/log-workspace"]
                                },
                                "alertContext": {
                                    "condition": {
                                        "allOf": [
                                            {
                                                "dimensions": [
                                                    { "name": "Site", "value": "BERLIN" },
                                                    { "name": "Status", "value": "Failed" },
                                                    { "name": "SourceDSA", "value": "SV253651" }
                                                ]
                                            }
                                        ]
                                    }
                                }
                            }
                        }
                        """);

                Assert.Equal("SV253651", alert.TargetName);
                Assert.Equal("BERLIN", alert.SiteName);
                Assert.Equal("SV253651 (BERLIN)", alert.TargetDisplayName);
        }

        [Fact]
        public void TargetAndSiteSupportComputerAndSourceDsaSiteAcrossCriteria()
        {
                var alert = CreateAlert("alert-1", "Fired", Now, rawJson: """
                        {
                            "data": {
                                "alertContext": {
                                    "condition": {
                                        "allOf": [
                                            {
                                                "dimensions": [
                                                    { "name": "Result", "value": "Failed" },
                                                    { "name": "SourceDSASite", "value": "MUNICH" }
                                                ]
                                            },
                                            {
                                                "dimensions": [
                                                    { "name": "Computer", "value": "DC-02" }
                                                ]
                                            }
                                        ]
                                    }
                                }
                            }
                        }
                        """);

                Assert.Equal("DC-02", alert.TargetName);
                Assert.Equal("MUNICH", alert.SiteName);
        }

        [Fact]
        public void TargetNameIgnoresEmptyDimensionPlaceholder()
        {
                var alert = CreateAlert("alert-1", "Fired", Now, rawJson: """
                        {
                            "data": {
                                "essentials": {
                                    "configurationItems": ["DOMAIN\\servers\\web-01"]
                                },
                                "alertContext": {
                                    "condition": {
                                        "allOf": [
                                            {
                                                "dimensions": [
                                                    { "name": "_ResourceId", "value": "<EMPTY_VALUE>" }
                                                ]
                                            }
                                        ]
                                    }
                                }
                            }
                        }
                        """);

                Assert.Equal("web-01", alert.TargetName);
                Assert.Equal("web-01", alert.TargetDisplayName);
        }

        [Fact]
        public void SearchQueryIsDecodedAndPreservesLineBreaks()
        {
                var alert = CreateAlert("alert-1", "Fired", Now, rawJson: """
                        {
                            "data": {
                                "alertContext": {
                                    "condition": {
                                        "allOf": [
                                            { "searchQuery": "Heartbeat\n| where TimeGenerated \u003e ago(5m)" }
                                        ]
                                    }
                                }
                            }
                        }
                        """);

                Assert.Equal("Heartbeat\n| where TimeGenerated > ago(5m)", alert.SearchQuery);
        }

    private static AlertRecord CreateAlert(
        string alertId,
        string condition,
        DateTimeOffset receivedAt,
        string comments = "",
        string rawJson = "{}") => new(
            Guid.NewGuid(),
            receivedAt,
            alertId,
            "Test alert",
            "Sev2",
            string.Empty,
            "Metric",
            condition,
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/fallback",
            "rg",
            "sub",
            receivedAt,
            "Description",
            string.Empty,
            comments,
            rawJson);
}