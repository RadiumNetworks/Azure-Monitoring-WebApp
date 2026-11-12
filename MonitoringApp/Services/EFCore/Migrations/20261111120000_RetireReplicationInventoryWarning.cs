using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261111120000_RetireReplicationInventoryWarning")]
public sealed class RetireReplicationInventoryWarning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE [AlertRules] SET [Enabled] = 0 WHERE [RuleType] = N'ReplicationInventoryWarning';");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
