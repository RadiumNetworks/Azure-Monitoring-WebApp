using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261021140000_AddAnalysisVisualizationOptions")]
public sealed class AddAnalysisVisualizationOptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "VisualizationJson", table: "AnalysisQueryDefinitions",
            type: "nvarchar(max)", nullable: false, defaultValue: "{}");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "VisualizationJson", table: "AnalysisQueryDefinitions");
}
