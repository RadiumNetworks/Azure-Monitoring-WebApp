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
                                                    { "name": "Site", "value": "Willowgate" },
                                                    { "name": "Status", "value": "Failed" },
                                                    { "name": "SourceDSA", "value": "DC-EMBER-05" }
                                                ]
                                            }
                                        ]
                                    }
                                }
                            }
                        }
                        """);

                Assert.Equal("DC-EMBER-05", alert.TargetName);
                Assert.Equal("Willowgate", alert.SiteName);
                Assert.Equal("DC-EMBER-05 (Willowgate)", alert.TargetDisplayName);
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
                                                    { "name": "SourceDSASite", "value": "Pinehaven" }
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
                Assert.Equal("Pinehaven", alert.SiteName);
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

                [Fact]
                public void QueryResultPresentationMapsDCDiagColumnsAndDecodesTestStates()
                {
                                var alert = CreateAlert("alert-1", "Fired", Now, rawJson: """
                                                {
                                                    "queryResult": {
                                                        "type": "DCDiag",
                                                        "computer": "DC-ORION-07",
                                                        "columns": [
                                                            { "name": "Site", "type": "string" },
                                                            { "name": "Status", "type": "dynamic" },
                                                            { "name": "TimeGenerated", "type": "datetime" },
                                                            { "name": "Computer", "type": "string" }
                                                        ],
                                                        "rows": [[
                                                            "Northbridge",
                                                            "{\"Advertising\":\"Passed\",\"DFSREvent\":\"Failed\",\"Services\":\"Passed\"}",
                                                            "2026-08-25T15:38:17.2836141Z",
                                                            "DC-ORION-07"
                                                        ]],
                                                        "rowCount": 1
                                                    }
                                                }
                                                """);

                                var result = Assert.IsType<QueryResultPresentation>(CreatePresenter().Parse(alert.RawJson));
                                Assert.Collection(
                                    result.Summary,
                                    badge => Assert.Equal("2 passed", badge.Text),
                                    badge => Assert.Equal("1 failed", badge.Text));
                                var row = Assert.Single(result.Rows);
                                Assert.Equal("DC-ORION-07", row.Title);
                                Assert.Contains(row.Metadata, value => value.Value == "Northbridge");
                                Assert.Contains(row.Metadata, value => value.Value == "2026-08-25T15:38:17.2836141Z");
                                var failure = Assert.Single(row.Alerts);
                                Assert.Equal("DFSREvent", failure.Label);
                                Assert.Equal("Failed", failure.Value);
                                var details = Assert.Single(row.Details);
                                Assert.Equal("All 3 tests", details.Label);
                                Assert.Collection(
                                    details.Items,
                                    item =>
                                        {
                                        Assert.Equal("DFSREvent", item.Label);
                                        Assert.Equal("failure", item.Tone);
                                        },
                                    item => Assert.Equal("Advertising", item.Label),
                                    item => Assert.Equal("Services", item.Label));
                }

                [Fact]
                public void QueryResultPresentationMapsReplicationColumnsAndState()
                {
                                var alert = CreateAlert("alert-1", "Fired", Now, rawJson: """
                                                {
                                                    "queryResult": {
                                                        "type": "Replication",
                                                        "sourceDSA": "DC-ATLAS-12",
                                                        "columns": [
                                                            { "name": "DestDSA", "type": "string" },
                                                            { "name": "NumberOfFailures", "type": "string" },
                                                            { "name": "SourceDSASite", "type": "string" },
                                                            { "name": "LastErrorStatus", "type": "string" },
                                                            { "name": "TimeGenerated", "type": "datetime" },
                                                            { "name": "NC", "type": "string" },
                                                            { "name": "LastSuccessTime", "type": "datetime" },
                                                            { "name": "SourceDSA", "type": "string" },
                                                            { "name": "DestDSASite", "type": "string" },
                                                            { "name": "Protocol", "type": "string" },
                                                            { "name": "LastFailureTime", "type": "string" }
                                                        ],
                                                        "rows": [[
                                                            "DC-LUMEN-04",
                                                            "2",
                                                            "Cedarfield",
                                                            "1722",
                                                            "2026-08-25T16:13:19.8154026Z",
                                                            "CN=Schema,CN=Configuration,DC=example,DC=com",
                                                            "2026-08-25T15:04:26Z",
                                                            "DC-ATLAS-12",
                                                            "Harborview",
                                                            "RPC",
                                                            "2026-08-25T16:04:26Z"
                                                        ]],
                                                        "rowCount": 1
                                                    }
                                                }
                                                """);

                                var result = Assert.IsType<QueryResultPresentation>(CreatePresenter().Parse(alert.RawJson));
                                Assert.Collection(
                                    result.Summary,
                                    badge => Assert.Equal("2 failures", badge.Text),
                                        badge => Assert.Equal("1 link", badge.Text));
                                var row = Assert.Single(result.Rows);
                                Assert.Equal("DC-ATLAS-12 to DC-LUMEN-04", row.Title);
                                Assert.Contains(row.Metadata, value => value.Value == "Cedarfield");
                                Assert.Contains(row.Metadata, value => value.Value == "Harborview");
                                Assert.Contains(row.Metadata, value => value.Value == "RPC");
                                Assert.Contains(row.Metadata, value => value.Value == "2026-08-25T16:13:19.8154026Z");
                                Assert.Contains(row.Alerts, item => item.Label == "Replication failures" && item.Value == "2");
                                Assert.Contains(row.Alerts, item => item.Label == "Error status" && item.Value == "1722");
                                Assert.Contains(row.Facts, value => value.Label == "Last failure" && value.Value == "2026-08-25T16:04:26Z");
                                Assert.Contains(row.Facts, value => value.Label == "Last success" && value.Value == "2026-08-25T15:04:26Z");
                                var details = Assert.Single(row.Details);
                                Assert.Equal("CN=Schema,CN=Configuration,DC=example,DC=com", Assert.Single(details.Items).Value);
                }

                    private static QueryResultPresenter CreatePresenter() =>
                    new(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));

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