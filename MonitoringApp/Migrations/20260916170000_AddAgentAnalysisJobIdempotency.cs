using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAnalysisJobIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "AgentAnalysisJobs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE [AgentAnalysisJobs] SET [IdempotencyKey] = CONCAT(N'LEGACY-', CONVERT(nvarchar(36), [Id])) WHERE [IdempotencyKey] = N'';");

            migrationBuilder.CreateIndex(
                name: "IX_AgentAnalysisJobs_IdempotencyKey",
                table: "AgentAnalysisJobs",
                column: "IdempotencyKey",
                unique: true,
                filter: "[Status] IN (N'Queued', N'Running', N'Completed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentAnalysisJobs_IdempotencyKey",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "AgentAnalysisJobs");
        }
    }
}
