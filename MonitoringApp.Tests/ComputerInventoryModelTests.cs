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
        var privateIpProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.PrivateIP)));
        var lastHeartbeatProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.LastHeartbeatAt)));
        var ntdsFileSizeProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.NtdsFileSizeInGb)));
        var certificatesProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.CertificatesJson)));
        var servicesProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.ServicesJson)));
        var maintenanceProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.Maintenance)));
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
        Assert.True(privateIpProperty.IsNullable);
        Assert.True(lastHeartbeatProperty.IsNullable);
        Assert.Equal(256, resourceGroupProperty.GetMaxLength());
        Assert.Equal(256, roleProperty.GetMaxLength());
        Assert.Equal(256, privateIpProperty.GetMaxLength());
        Assert.Equal("datetimeoffset", lastHeartbeatProperty.GetColumnType());
        var lastBootTimeProperty = Assert.IsAssignableFrom<IProperty>(entity.FindProperty(nameof(ComputerInventoryEntry.LastBootTime)));
        Assert.True(lastBootTimeProperty.IsNullable);
        Assert.Equal("datetimeoffset", lastBootTimeProperty.GetColumnType());
        Assert.True(ntdsFileSizeProperty.IsNullable);
        Assert.Equal("float", ntdsFileSizeProperty.GetColumnType());
        Assert.False(certificatesProperty.IsNullable);
        Assert.Equal("[]", certificatesProperty.GetDefaultValue());
        Assert.False(servicesProperty.IsNullable);
        Assert.Equal("[]", servicesProperty.GetDefaultValue());
        Assert.False(maintenanceProperty.IsNullable);
        Assert.Equal(false, maintenanceProperty.GetDefaultValue());
        Assert.False(subscriptionProperty.IsNullable);
        Assert.False(computerProperty.IsNullable);
    }

    [Fact]
    public void BootTimeMigrationOnlyAddsNullableColumnAndSnapshotMatchesModel()
    {
        var migration = new MonitoringApp.Services.EFCore.Migrations.AddInventoryLastBootTime();
        var operation = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("ComputerInventory", operation.Table);
        Assert.Equal("LastBootTime", operation.Name);
        Assert.Equal("datetimeoffset", operation.ColumnType);
        Assert.True(operation.IsNullable);
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;").Options);
        Assert.False(context.Database.HasPendingModelChanges());
    }
}