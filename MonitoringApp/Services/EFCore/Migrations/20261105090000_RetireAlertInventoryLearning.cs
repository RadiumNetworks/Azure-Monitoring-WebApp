using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261105090000_RetireAlertInventoryLearning")]
public sealed class RetireAlertInventoryLearning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE [AlertRules] SET [Enabled] = 0
            WHERE [RuleType] = N'InventoryRoleAssignment';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
