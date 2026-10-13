using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertRuleAnalysisClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnalysisClassification",
                table: "AlertRules",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: AlertRuleAnalysisClassifications.None);

            migrationBuilder.Sql(
                $"""
                UPDATE [AlertRules]
                SET [AnalysisClassification] = N'{AlertRuleAnalysisClassifications.SystemOutage}'
                WHERE [Id] = '47a96c56-ccf5-4f4e-97ce-6a72bb462f91';

                UPDATE [AlertRules]
                SET [AnalysisClassification] = N'{AlertRuleAnalysisClassifications.Suppressed}'
                WHERE [Id] IN (
                    'd82b566a-ce6e-4201-b7b9-9a366426e7b8',
                    'd82b566a-ce6e-4201-b7b9-9a366426e7b9');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisClassification",
                table: "AlertRules");
        }
    }
}
