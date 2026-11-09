using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class PersistAgentAnalysisResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetailedAnalysisJson",
                table: "AgentAnalysisJobs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "AgentAnalysisJobs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResultStoredAt",
                table: "AgentAnalysisJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceIdsJson",
                table: "AgentAnalysisJobs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TeamsDeliveredAt",
                table: "AgentAnalysisJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeamsDeliveredResultVersion",
                table: "AgentAnalysisJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamsDeliveryStatus",
                table: "AgentAnalysisJobs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotRequested");

            migrationBuilder.AddColumn<string>(
                name: "TeamsSummaryJson",
                table: "AgentAnalysisJobs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetailedAnalysisJson",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "ResultStoredAt",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "SourceIdsJson",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "TeamsDeliveredAt",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "TeamsDeliveredResultVersion",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "TeamsDeliveryStatus",
                table: "AgentAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "TeamsSummaryJson",
                table: "AgentAnalysisJobs");
        }
    }
}
