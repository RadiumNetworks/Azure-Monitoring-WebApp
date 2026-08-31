using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class ParsedAlertRecordTests
{
    [Fact]
    public void ExtractsAlertFieldsAndInventoryReference()
    {
        const string rawJson = """
            {
              "originalAlertId":"original-42",
              "data":{"alertContext":{"condition":{"allOf":[{
                "searchQuery":"Heartbeat | where Computer == 'DC-01'",
                "dimensions":[{"name":"Computer","value":"DC-01"},{"name":"Site","value":"North"}]
              }]}}},
              "queryResult":{"type":"Heartbeat","columns":[{"name":"Computer"}],"rows":[["DC-01"]]}
            }
            """;
        var id = Guid.NewGuid();
        var firedAt = DateTimeOffset.Parse("2026-08-31T01:02:03Z");
        var alert = new AlertRecord(
            id, firedAt, "alert-42", "Heartbeat missing", "Sev2", "Fired", "Metric",
            "Fired", "/subscriptions/sub-1/resourceGroups/rg-1/providers/test/machines/DC-01",
            "rg-1", "sub-1", firedAt, string.Empty, string.Empty, string.Empty, rawJson)
        {
            DisplayIdentity = new AlertDisplayIdentity("DC-01", "North")
        };

        var parsed = ParsedAlertFactory.Create(alert);

        Assert.Equal(id, parsed.Id);
        Assert.Equal(firedAt, parsed.FiredDateTime);
        Assert.Equal("alert-42", parsed.AlertId);
        Assert.Equal("original-42", parsed.OriginalAlertId);
        Assert.Equal("sub-1", parsed.InventorySubscriptionId);
        Assert.Equal("DC-01", parsed.InventoryComputer);
        Assert.Equal(alert.SearchQuery, parsed.SearchQuery);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(parsed.Dimensions).RootElement.ValueKind);
        Assert.Equal("Heartbeat", JsonDocument.Parse(parsed.QueryResults).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void MapsManyParsedAlertsToOneInventoryComputer()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(ParsedAlertRecord)));
        var foreignKey = Assert.Single(entity.GetForeignKeys(), key =>
            key.PrincipalEntityType.ClrType == typeof(ComputerInventoryEntry));

        Assert.Equal(
            [nameof(ParsedAlertRecord.InventorySubscriptionId), nameof(ParsedAlertRecord.InventoryComputer)],
            foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(ComputerInventoryEntry.SubscriptionId), nameof(ComputerInventoryEntry.Computer)],
            foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.False(foreignKey.IsUnique);
        Assert.Equal(DeleteBehavior.SetNull, foreignKey.DeleteBehavior);
    }
}
