using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryStandardTextProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StandardTextProvenanceJson",
                table: "ScheduledQueryRules",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StandardTextProvenanceJson",
                table: "AnalysisQueryVersions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StandardTextProvenanceJson",
                table: "AnalysisQueryDefinitions",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StandardTextProvenanceJson",
                table: "ScheduledQueryRules");

            migrationBuilder.DropColumn(
                name: "StandardTextProvenanceJson",
                table: "AnalysisQueryVersions");

            migrationBuilder.DropColumn(
                name: "StandardTextProvenanceJson",
                table: "AnalysisQueryDefinitions");
        }
    }
}
