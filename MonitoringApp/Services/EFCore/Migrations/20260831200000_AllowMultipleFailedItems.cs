using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831200000_AllowMultipleFailedItems")]
public sealed class AllowMultipleFailedItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "UPDATE dbo.AlertRules SET ConditionType = N'OnlyFailedItems' WHERE ConditionType = N'OnlyFailedItem';");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "UPDATE dbo.AlertRules SET ConditionType = N'OnlyFailedItem' WHERE ConditionType = N'OnlyFailedItems';");
}
