using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryHeartbeatStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHeartbeatAt",
                table: "ComputerInventory",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivateIP",
                table: "ComputerInventory",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.InsertData(
                table: "AlertRules",
                columns:
                [
                    "Id", "Name", "Enabled", "Priority", "RuleType", "AlertNameContains",
                    "QueryResultType", "ConditionType", "Threshold", "FailedItemName",
                    "CategoryName", "ApplyToTarget", "Collapsed", "Tone", "InventoryRole",
                    "IsCritical"
                ],
                values:
                [
                    new Guid("7b28f935-087b-4c20-8635-84e2c65484e9"),
                    "Inventory Heartbeat Status", true, 1, AlertRuleTypes.InventoryStatus, "Inventory Heartbeat Status",
                    InventoryHeartbeatBatch.QueryResultType, AlertRuleConditionTypes.AnyMatchingAlert, 0, "",
                    "", false, false, "info", "", false
                ]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AlertRules",
                keyColumn: "Id",
                keyValue: new Guid("7b28f935-087b-4c20-8635-84e2c65484e9"));

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "ComputerInventory");

            migrationBuilder.DropColumn(
                name: "PrivateIP",
                table: "ComputerInventory");
        }
    }
}
