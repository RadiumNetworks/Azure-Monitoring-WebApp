using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261021173000_AddAnalysisQueryCategories")]
public sealed class AddAnalysisQueryCategories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AnalysisQueryCategories",
            columns: table => new
            {
                Path = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AnalysisQueryCategories", category => category.Path));
        migrationBuilder.Sql("INSERT INTO [AnalysisQueryCategories] ([Path]) SELECT DISTINCT [Category] FROM [AnalysisQueryDefinitions];");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AnalysisQueryCategories");
}
