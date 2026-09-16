using System.Text.Json;

namespace MonitoringApp.Tests;

public sealed class InventoryHeartbeatBatchTests
{
  [Fact]
  public void RecognizesStoredLegacyInventoryAlertsByNameOrPayloadType()
  {
    var namedAlert = CreateAlert("Inventory Heartbeat Status", "{}");
    var typedAlert = CreateAlert("Legacy scheduled status", """
      { "queryResult": { "type": "InventoryHeartbeat" } }
      """);
    var ordinaryAlert = CreateAlert("CPU alert", "{}");

    Assert.True(InventoryHeartbeatBatch.IsInventoryHeartbeat(namedAlert));
    Assert.True(InventoryHeartbeatBatch.IsInventoryHeartbeat(typedAlert));
    Assert.False(InventoryHeartbeatBatch.IsInventoryHeartbeat(ordinaryAlert));
  }

    [Fact]
    public void ParsesVerifiedBatchShapeAndKeepsNewestDuplicate()
    {
        using var document = JsonDocument.Parse("""
            {
              "queryResult": {
                "type": "InventoryHeartbeat",
                "columns": [
                  { "name": "LastHeartbeat", "type": "datetime" },
                  { "name": "Computer", "type": "string" },
                  { "name": "Domain", "type": "string" },
                  { "name": "PrivateIP", "type": "string" },
                  { "name": "SubscriptionId", "type": "string" }
                ],
                "rows": [
                  ["2026-09-14T09:40:00Z", "SERVER01", "example.test", "10.0.0.1", "subscription-a"],
                  ["2026-09-14T09:55:00Z", "server01", "example.test", "10.0.0.2", "SUBSCRIPTION-A"]
                ]
              }
            }
            """);

        Assert.True(InventoryHeartbeatBatch.IsInventoryHeartbeat(document.RootElement));
        var observation = Assert.Single(InventoryHeartbeatBatch.Parse(document.RootElement));
        Assert.Equal("SUBSCRIPTION-A", observation.SubscriptionId);
        Assert.Equal("server01", observation.Computer);
        Assert.Equal("example.test", observation.Domain);
        Assert.Equal("10.0.0.2", observation.PrivateIP);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T09:55:00Z"), observation.LastHeartbeatAt);
    }

    [Fact]
    public void SkipsRowsWithoutInventoryIdentityOrHeartbeat()
    {
        using var document = JsonDocument.Parse("""
            {
              "queryResult": {
                "type": "InventoryHeartbeat",
                "columns": [
                  { "name": "SubscriptionId" },
                  { "name": "Computer" },
                  { "name": "LastHeartbeat" }
                ],
                "rows": [
                  ["", "SERVER01", "2026-09-14T09:55:00Z"],
                  ["subscription-a", "", "2026-09-14T09:55:00Z"],
                  ["subscription-a", "SERVER01", "invalid"]
                ]
              }
            }
            """);

        Assert.Empty(InventoryHeartbeatBatch.Parse(document.RootElement));
    }

      private static AlertRecord CreateAlert(string name, string rawJson) => new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        string.Empty,
        name,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        string.Empty,
        string.Empty,
        string.Empty,
        rawJson);
}