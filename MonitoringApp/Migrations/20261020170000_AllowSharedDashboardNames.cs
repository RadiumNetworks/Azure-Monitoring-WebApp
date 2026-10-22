using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261020170000_AllowSharedDashboardNames")]
public sealed class AllowSharedDashboardNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_AnalysisDashboardTemplates_Name_Version", "AnalysisDashboardTemplates");
        migrationBuilder.CreateIndex("IX_AnalysisDashboardTemplates_Name_Version", "AnalysisDashboardTemplates", ["Name", "Version"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_AnalysisDashboardTemplates_Name_Version", "AnalysisDashboardTemplates");
        migrationBuilder.CreateIndex("IX_AnalysisDashboardTemplates_Name_Version", "AnalysisDashboardTemplates", ["Name", "Version"], unique: true);
    }
}
