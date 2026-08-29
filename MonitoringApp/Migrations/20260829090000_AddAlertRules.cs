using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260829090000_AddAlertRules")]
public sealed class AddAlertRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AlertRules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Enabled = table.Column<bool>(type: "bit", nullable: false),
                Priority = table.Column<int>(type: "int", nullable: false),
                AlertNameContains = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                QueryResultType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ConditionType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Threshold = table.Column<int>(type: "int", nullable: false),
                FailedItemName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CategoryName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                ApplyToTarget = table.Column<bool>(type: "bit", nullable: false),
                Collapsed = table.Column<bool>(type: "bit", nullable: false),
                Tone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AlertRules", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AlertRules_Enabled_Priority",
            table: "AlertRules",
            columns: new[] { "Enabled", "Priority" });
        migrationBuilder.CreateIndex(
            name: "IX_AlertRules_Name",
            table: "AlertRules",
            column: "Name",
            unique: true);

        migrationBuilder.InsertData(
            table: "AlertRules",
            columns:
            [
                "Id", "Name", "Enabled", "Priority", "AlertNameContains", "QueryResultType",
                "ConditionType", "Threshold", "FailedItemName", "CategoryName", "ApplyToTarget",
                "Collapsed", "Tone"
            ],
            columnTypes:
            [
                "uniqueidentifier", "nvarchar(256)", "bit", "int", "nvarchar(256)", "nvarchar(128)",
                "nvarchar(64)", "int", "nvarchar(256)", "nvarchar(256)", "bit", "bit", "nvarchar(32)"
            ],
            values: new object[,]
            {
                {
                    new Guid("47a96c56-ccf5-4f4e-97ce-6a72bb462f91"),
                    "Port failures indicate system outage", true, 10, "Port", "DCPort",
                    AlertRuleConditionTypes.RowCountGreaterThan, 10, "", "System Outage", true,
                    true, "failure"
                },
                {
                    new Guid("d82b566a-ce6e-4201-b7b9-9a366426e7b8"),
                    "Suppress isolated DFSREvent failure", true, 20, "DCDiag", "DCDiag",
                    AlertRuleConditionTypes.OnlyFailedItem, 0, "DFSREvent", "Suppressed alerts", false,
                    true, "info"
                }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AlertRules");
}