using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261009102400_AddHealthyDCPortSuppressionRule")]
public sealed class AddHealthyDCPortSuppressionRule : Migration
{
    private static readonly Guid RuleId = new("d82b566a-ce6e-4201-b7b9-9a366426e7ba");

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.InsertData(
            table: "AlertRules",
            columns:
            [
                "Id", "Name", "Enabled", "Priority", "RuleType", "AlertNameContains",
                "QueryResultType", "ConditionType", "Threshold", "FailedItemName",
                "CategoryName", "ApplyToTarget", "Collapsed", "Tone", "InventoryRole",
                "IsCritical", "AnalysisClassification"
            ],
            columnTypes:
            [
                "uniqueidentifier", "nvarchar(256)", "bit", "int", "nvarchar(64)",
                "nvarchar(256)", "nvarchar(128)", "nvarchar(64)", "int", "nvarchar(256)",
                "nvarchar(256)", "bit", "bit", "nvarchar(32)", "nvarchar(256)", "bit",
                "nvarchar(64)"
            ],
            values:
            [
                RuleId, "Suppress healthy DCPort results", true, 5,
                AlertRuleTypes.Categorization, "", "DCPort",
                AlertRuleConditionTypes.RowCountEquals, 0, "", "Suppressed alerts",
                false, true, "info", "", false, AlertRuleAnalysisClassifications.Suppressed
            ]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DeleteData(
            table: "AlertRules",
            keyColumn: "Id",
            keyColumnType: "uniqueidentifier",
            keyValue: RuleId);
}
