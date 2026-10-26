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
                  { "name": "SubscriptionId", "type": "string" },
                  { "name": "Certificates", "type": "dynamic" },
                  { "name": "NTDSFileSizeinGB", "type": "real" },
                  { "name": "LDAPSCertificate", "type": "dynamic" },
                  { "name": "Services", "type": "dynamic" },
                  { "name": "TenantId", "type": "string" },
                  { "name": "Site", "type": "string" }
                ],
                "rows": [
                  ["2026-09-14T09:40:00Z", "SERVER01", "example.test", "10.0.0.1", "subscription-a", null, null, null, null, "tenant-a", "Old Site"],
                  [
                    "2026-09-14T09:55:00Z",
                    "server01",
                    "example.test",
                    "10.0.0.2",
                    "SUBSCRIPTION-A",
                    "[{\"Subject\":\"CN=MDI\",\"Thumbprint\":\"A1\",\"NotAfter\":\"2028-10-08T00:00:00Z\"}]",
                    12.5,
                    "{\"Subject\":\"CN=server01.example.test\",\"SerialNumber\":\"42\",\"NotAfter\":\"2027-09-24T11:20:43Z\"}",
                    "[{\"Name\":\"NTDS\",\"Status\":4},{\"Name\":\"Kdc\",\"Status\":1}]",
                    "tenant-a",
                    "Berlin"
                  ]
                ]
              }
            }
            """);

        Assert.True(InventoryHeartbeatBatch.IsInventoryHeartbeat(document.RootElement));
        var observation = Assert.Single(InventoryHeartbeatBatch.Parse(document.RootElement));
        Assert.Equal("SUBSCRIPTION-A", observation.SubscriptionId);
        Assert.Equal("server01", observation.Computer);
        Assert.Equal("example.test", observation.Domain);
        Assert.Equal("Berlin", observation.Site);
        Assert.Equal("10.0.0.2", observation.PrivateIP);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T09:55:00Z"), observation.LastHeartbeatAt);
        Assert.Equal(12.5, observation.NtdsFileSizeInGb);
        Assert.True(observation.IsDomainController);
        Assert.Equal("Domain Controller", observation.DetectedRole);
        Assert.NotNull(observation.CertificatesJson);
        Assert.NotNull(observation.ServicesJson);
        var inventory = new ComputerInventoryEntry
        {
            CertificatesJson = observation.CertificatesJson!,
            ServicesJson = observation.ServicesJson!
        };
        Assert.Collection(
            inventory.Certificates,
            certificate =>
            {
                Assert.Equal("CN=MDI", certificate.Name);
                Assert.Equal("A1", certificate.SerialNumber);
                Assert.Equal(DateTimeOffset.Parse("2028-10-08T00:00:00Z"), certificate.ExpiresAt);
            },
            certificate =>
            {
                Assert.Equal("CN=server01.example.test", certificate.Name);
                Assert.Equal("42", certificate.SerialNumber);
                Assert.Equal(DateTimeOffset.Parse("2027-09-24T11:20:43Z"), certificate.ExpiresAt);
            });
        Assert.Equal(
            [new InventoryService("NTDS", 4), new InventoryService("Kdc", 1)],
            inventory.Services);
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

    [Fact]
    public void NtdsServiceIdentifiesDomainControllerWithoutDatabaseSize()
    {
        using var document = JsonDocument.Parse("""
            {
              "queryResult": {
                "type": "InventoryHeartbeat",
                "columns": [
                  { "name": "SubscriptionId" },
                  { "name": "Computer" },
                  { "name": "LastHeartbeat" },
                  { "name": "Services" }
                ],
                "rows": [[
                  "subscription-a",
                  "SERVER01",
                  "2026-10-12T12:10:47Z",
                  "[{\"Name\":\"NTDS\",\"Status\":4}]"
                ]]
              }
            }
            """);

        var observation = Assert.Single(InventoryHeartbeatBatch.Parse(document.RootElement));

        Assert.True(observation.IsDomainController);
        Assert.Equal("Domain Controller", observation.DetectedRole);
        Assert.Null(observation.NtdsFileSizeInGb);
        Assert.False(observation.HasNtdsFileSizeData);
        Assert.Null(observation.CertificatesJson);
        Assert.NotNull(observation.ServicesJson);
    }

    [Theory]
    [InlineData("AzureADConnectHealthAgent", 4, false, true)]
    [InlineData("azureadconnecthealthagent", 1, true, true)]
    [InlineData("AZUREADCONNECTHEALTHAGENT", null, false, true)]
    [InlineData("AzureADConnectHealthAgentUpdater", 4, true, false)]
    [InlineData("OtherService", 4, false, false)]
    public void DetectsEntraConnectFromExactServiceNameRegardlessOfStatus(
        string name, int? status, bool encoded, bool expected)
    {
        var observation = ParseServices([new(name, status)], encoded);

        Assert.Equal(expected, observation.IsEntraConnect);
        Assert.Equal(expected ? "Entra Connect" : null, observation.DetectedRole);
        Assert.False(observation.IsDomainController);
    }

    [Fact]
    public void EntraConnectServiceTakesPrecedenceWhenNtdsIsAlsoReported()
    {
        var observation = ParseServices(
            [new("NTDS", 4), new("AzureADConnectHealthAgent", 1)], false);

        Assert.True(observation.IsDomainController);
        Assert.True(observation.IsEntraConnect);
        Assert.Equal("Entra Connect", observation.DetectedRole);
    }

    private static InventoryHeartbeatObservation ParseServices(InventoryService[] services, bool encoded)
    {
        var servicesValue = JsonSerializer.SerializeToElement(services);
        if (encoded)
            servicesValue = JsonSerializer.SerializeToElement(servicesValue.GetRawText());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            queryResult = new
            {
                type = InventoryHeartbeatBatch.QueryResultType,
                columns = new[]
                {
                    new { name = "SubscriptionId" }, new { name = "Computer" },
                    new { name = "LastHeartbeat" }, new { name = "Services" }
                },
                rows = new[] { new object[] { "sub", "SERVER01", "2026-10-23T12:00:00Z", servicesValue } }
            }
        }));
        return Assert.Single(InventoryHeartbeatBatch.Parse(document.RootElement));
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