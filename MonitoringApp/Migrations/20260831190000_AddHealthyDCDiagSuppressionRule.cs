using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831190000_AddHealthyDCDiagSuppressionRule")]
public sealed class AddHealthyDCDiagSuppressionRule : Migration
{
    private static readonly Guid RuleId = new("d82b566a-ce6e-4201-b7b9-9a366426e7b9");

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.InsertData(
            table: "AlertRules",
            columns:
            [
                "Id", "Name", "Enabled", "Priority", "RuleType", "AlertNameContains",
                "QueryResultType", "ConditionType", "Threshold", "FailedItemName",
                "CategoryName", "ApplyToTarget", "Collapsed", "Tone", "InventoryRole",
                "IsCritical"
            ],
            columnTypes:
            [
                "uniqueidentifier", "nvarchar(256)", "bit", "int", "nvarchar(64)",
                "nvarchar(256)", "nvarchar(128)", "nvarchar(64)", "int", "nvarchar(256)",
                "nvarchar(256)", "bit", "bit", "nvarchar(32)", "nvarchar(256)", "bit"
            ],
            values:
            [
                RuleId, "Suppress healthy DCDiag results", true, 30,
                AlertRuleTypes.Categorization, "DCDiag", "DCDiag",
                AlertRuleConditionTypes.NoFailedItems, 0, "", "Suppressed alerts",
                false, true, "info", "", false
            ]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DeleteData(
            table: "AlertRules",
            keyColumn: "Id",
            keyColumnType: "uniqueidentifier",
            keyValue: RuleId);
}
