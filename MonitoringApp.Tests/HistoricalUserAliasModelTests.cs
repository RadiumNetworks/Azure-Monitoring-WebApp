using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MonitoringApp.Services.EFCore.Migrations;

namespace MonitoringApp.Tests;

public sealed class HistoricalUserAliasModelTests
{
    internal const string MigrationId = "20261111131740_AddHistoricalUserAliases";

    [Fact]
    public void ModelKeepsDisplayAliasesSeparateFromVerifiedIdentityLinksAndDoesNotCascade()
    {
        using var context = Context();
        var entity = context.Model.FindEntityType(typeof(HistoricalUserAlias))!;
        Assert.Equal("HistoricalUserAliases", entity.GetTableName());
        Assert.Equal(nameof(HistoricalUserAlias.AliasUserId), Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
        Assert.Equal(2, entity.GetForeignKeys().Count());
        Assert.All(entity.GetForeignKeys(), key =>
        {
            Assert.Equal(typeof(DirectoryUser), key.PrincipalEntityType.ClrType);
            Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior);
        });
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void MigrationOnlyAddsEmptyAliasSchemaAndBlocksDestructiveDowngrades()
    {
        var migration = new AddHistoricalUserAliases();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("HistoricalUserAliases", table.Name);
        Assert.Contains(table.CheckConstraints, constraint => constraint.Sql == "[AliasUserId] <> [MainUserId]");
        Assert.All(migration.UpOperations, operation => Assert.True(operation is CreateTableOperation or CreateIndexOperation));
        Assert.Contains(migration.DownOperations.OfType<SqlOperation>(), operation =>
            operation.Sql.Contains("IF EXISTS (SELECT 1 FROM [HistoricalUserAliases])", StringComparison.Ordinal) &&
            operation.Sql.Contains("THROW", StringComparison.Ordinal));
    }

    [Fact]
    public void UpgradeAndFreshInstallContainTheExactGeneratedIdempotentMigration()
    {
        using var context = Context();
        var expected = context.GetService<IMigrator>().GenerateScript("20261111120000_RetireReplicationInventoryWarning",
            MigrationId, MigrationsSqlGenerationOptions.Idempotent).Trim();
        var scripts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "MonitoringApp", "Services", "EFCore", "Scripts"));
        Assert.Equal(expected, File.ReadAllText(Path.Combine(scripts, "UpgradeHistoricalUserAliases.sql")).Trim());
        Assert.Contains(expected, File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql")));
    }

    private static AlertDbContext Context() => new(new DbContextOptionsBuilder<AlertDbContext>()
        .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True").Options);
}
