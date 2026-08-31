using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MonitoringApp.Tests;

public sealed class ComputerInventoryModelTests
{
    [Fact]
    public void UsesSubscriptionAndComputerAsCompositePrimaryKey()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = context.Model.FindEntityType(typeof(ComputerInventoryEntry));
        var primaryKey = entity?.FindPrimaryKey();

        Assert.NotNull(entity);
        var domainProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.Domain)));
        var siteProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.Site)));
        var resourceGroupProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.ResourceGroup)));
        var roleProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.Role)));
        var subscriptionProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.SubscriptionId)));
        var computerProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.Computer)));

        Assert.Equal("ComputerInventory", entity.GetTableName());
        Assert.Equal(
            [nameof(ComputerInventoryEntry.SubscriptionId), nameof(ComputerInventoryEntry.Computer)],
            primaryKey?.Properties.Select(property => property.Name));
        Assert.True(domainProperty.IsNullable);
        Assert.True(siteProperty.IsNullable);
        Assert.True(resourceGroupProperty.IsNullable);
        Assert.True(roleProperty.IsNullable);
        Assert.Equal(256, resourceGroupProperty.GetMaxLength());
        Assert.Equal(256, roleProperty.GetMaxLength());
        Assert.False(subscriptionProperty.IsNullable);
        Assert.False(computerProperty.IsNullable);
    }
}