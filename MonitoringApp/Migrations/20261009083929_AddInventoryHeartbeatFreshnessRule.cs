using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryHeartbeatFreshnessRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_RuleType",
                table: "AlertRules",
                column: "RuleType",
                unique: true,
                filter: "[RuleType] = 'InventoryHeartbeatFreshness'");

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM [AlertRules] WHERE [RuleType] = N'InventoryHeartbeatFreshness')
                BEGIN
                    INSERT INTO [AlertRules]
                        ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains],
                         [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName],
                         [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical], [AnalysisClassification])
                    VALUES
                        ('d7ab8e49-2cfa-4a2e-a635-964bee230001', N'Inventory heartbeat freshness', 1, 0,
                         N'InventoryHeartbeatFreshness', N'', N'', N'', 5, N'', N'',
                         0, 0, N'info', N'', 0, N'None');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM [AlertRules]
                WHERE [Id] = 'd7ab8e49-2cfa-4a2e-a635-964bee230001'
                    AND [RuleType] = N'InventoryHeartbeatFreshness';
                """);

            migrationBuilder.DropIndex(
                name: "IX_AlertRules_RuleType",
                table: "AlertRules");
        }
    }
}
