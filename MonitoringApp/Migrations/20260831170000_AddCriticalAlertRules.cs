using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831170000_AddCriticalAlertRules")]
public sealed class AddCriticalAlertRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsCritical",
            table: "AlertRules",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.UpdateData(
            table: "AlertRules",
            keyColumn: "Id",
            keyValue: new Guid("47a96c56-ccf5-4f4e-97ce-6a72bb462f91"),
            column: "IsCritical",
            value: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "IsCritical", table: "AlertRules");
}
