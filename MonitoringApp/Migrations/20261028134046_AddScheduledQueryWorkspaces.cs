using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledQueryWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkspaceKey",
                table: "ScheduledQueryTargetStates",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LastWorkspaceResultsJson",
                table: "ScheduledQueryRules",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceKeysJson",
                table: "ScheduledQueryRules",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql("""
                UPDATE [ScheduledQueryTargetStates]
                SET [WorkspaceKey] = (SELECT [WorkspaceKey] FROM [ScheduledQueryRules]
                    WHERE [Id] = [ScheduledQueryTargetStates].[RuleId]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkspaceKey",
                table: "ScheduledQueryTargetStates");

            migrationBuilder.DropColumn(
                name: "LastWorkspaceResultsJson",
                table: "ScheduledQueryRules");

            migrationBuilder.DropColumn(
                name: "WorkspaceKeysJson",
                table: "ScheduledQueryRules");
        }
    }
}
