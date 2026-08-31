using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class ParsedAlertRecordTests
{
    private static readonly ParsedAlertRecordTestCases TestCases =
        TestCaseLoader.Load<ParsedAlertRecordTestCases>("parsed-alert-record.json");

    [Fact]
    public void ExtractsAlertFieldsAndInventoryReference()
    {
        var alert = TestAlertFactory.FromFixture(TestCases.Alert) with
        {
            DisplayIdentity = new AlertDisplayIdentity(TestCases.ExpectedInventoryComputer, "North")
        };

        var parsed = ParsedAlertFactory.Create(alert);

        Assert.Equal(alert.Id, parsed.Id);
        Assert.Equal(TestCases.Alert.FiredAt, parsed.FiredDateTime);
        Assert.Equal(TestCases.Alert.AlertId, parsed.AlertId);
        Assert.Equal(TestCases.ExpectedOriginalAlertId, parsed.OriginalAlertId);
        Assert.Equal(TestCases.ExpectedInventorySubscriptionId, parsed.InventorySubscriptionId);
        Assert.Equal(TestCases.ExpectedInventoryComputer, parsed.InventoryComputer);
        Assert.Equal(alert.SearchQuery, parsed.SearchQuery);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(parsed.Dimensions).RootElement.ValueKind);
        Assert.Equal(TestCases.ExpectedQueryResultType, JsonDocument.Parse(parsed.QueryResults).RootElement.GetProperty("type").GetString());
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
