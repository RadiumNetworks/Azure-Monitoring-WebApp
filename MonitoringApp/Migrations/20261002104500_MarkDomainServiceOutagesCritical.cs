using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261002104500_MarkDomainServiceOutagesCritical")]
public sealed class MarkDomainServiceOutagesCritical : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            UPDATE [AlertRules]
            SET [IsCritical] = CAST(1 AS bit),
                [AnalysisClassification] = N'SystemOutage'
            WHERE [Id] IN
            (
                'c4c346f4-2f67-465e-94b0-c018a6de0001',
                'c4c346f4-2f67-465e-94b0-c018a6de0002'
            );
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            UPDATE [AlertRules]
            SET [IsCritical] = CAST(0 AS bit),
                [AnalysisClassification] = N'None'
            WHERE [Id] IN
            (
                'c4c346f4-2f67-465e-94b0-c018a6de0001',
                'c4c346f4-2f67-465e-94b0-c018a6de0002'
            );
            """);
}
