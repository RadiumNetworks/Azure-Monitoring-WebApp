using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261019142000_NormalizeDomainControllerRole")]
public sealed class NormalizeDomainControllerRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE [ComputerInventory]
            SET [Role] = N'Domain Controller'
            WHERE LOWER(LTRIM(RTRIM([Role]))) = N'domaincontrollers';

            UPDATE [AlertRules]
            SET [InventoryRole] = N'Domain Controller'
            WHERE LOWER(LTRIM(RTRIM([InventoryRole]))) = N'domaincontrollers';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
