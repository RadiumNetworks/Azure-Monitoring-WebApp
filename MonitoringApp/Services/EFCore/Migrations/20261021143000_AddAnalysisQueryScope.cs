using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261021143000_AddAnalysisQueryScope")]
public sealed class AddAnalysisQueryScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "Scope", table: "AnalysisQueryDefinitions",
            type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "SelectedSystems");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "Scope", table: "AnalysisQueryDefinitions");
}
