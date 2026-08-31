using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831150000_AddInventoryRolesAndFilters")]
public sealed class AddInventoryRolesAndFilters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ResourceGroup",
            table: "ComputerInventory",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Role",
            table: "ComputerInventory",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "RuleType",
            table: "AlertRules",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: AlertRuleTypes.Categorization);
        migrationBuilder.AddColumn<string>(
            name: "InventoryRole",
            table: "AlertRules",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.InsertData(
            table: "AlertRules",
            columns:
            [
                "Id", "Name", "Enabled", "Priority", "RuleType", "AlertNameContains",
                "QueryResultType", "ConditionType", "Threshold", "FailedItemName", "CategoryName",
                "ApplyToTarget", "Collapsed", "Tone", "InventoryRole"
            ],
            columnTypes:
            [
                "uniqueidentifier", "nvarchar(256)", "bit", "int", "nvarchar(64)", "nvarchar(256)",
                "nvarchar(128)", "nvarchar(64)", "int", "nvarchar(256)", "nvarchar(256)",
                "bit", "bit", "nvarchar(32)", "nvarchar(256)"
            ],
            values: new object[,]
            {
                {
                    new Guid("956fbb9c-cadb-4f49-ad1e-78c09a8a1301"),
                    "DCDiag targets are domain controllers", true, 100,
                    AlertRuleTypes.InventoryRoleAssignment, "", "DCDiag", "", 0, "", "",
                    false, false, "info", "domaincontrollers"
                },
                {
                    new Guid("956fbb9c-cadb-4f49-ad1e-78c09a8a1302"),
                    "Replication targets are domain controllers", true, 110,
                    AlertRuleTypes.InventoryRoleAssignment, "", "Replication", "", 0, "", "",
                    false, false, "info", "domaincontrollers"
                }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DeleteData(
            table: "AlertRules",
            keyColumn: "Id",
            keyValues:
            [
                new Guid("956fbb9c-cadb-4f49-ad1e-78c09a8a1301"),
                new Guid("956fbb9c-cadb-4f49-ad1e-78c09a8a1302")
            ]);
        migrationBuilder.DropColumn(name: "ResourceGroup", table: "ComputerInventory");
        migrationBuilder.DropColumn(name: "Role", table: "ComputerInventory");
        migrationBuilder.DropColumn(name: "RuleType", table: "AlertRules");
        migrationBuilder.DropColumn(name: "InventoryRole", table: "AlertRules");
    }
}
