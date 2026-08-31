using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831180000_AddParsedAlertCriticalLifecycle")]
public sealed class AddParsedAlertCriticalLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsCritical",
            table: "ParsedAlerts",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ResolvedAt",
            table: "ParsedAlerts",
            type: "datetimeoffset",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE fired
            SET fired.IsCritical = 1
            FROM dbo.ParsedAlerts AS fired
            WHERE fired.MonitorCondition = N'Fired'
              AND fired.AlertName LIKE N'%Port%'
              AND JSON_VALUE(fired.QueryResults, '$.type') = N'DCPort'
              AND (SELECT COUNT(*) FROM OPENJSON(fired.QueryResults, '$.rows')) > 10;

            ;WITH Resolutions AS
            (
                SELECT parsed.AlertId, MAX(alerts.ReceivedAt) AS ResolvedAt
                FROM dbo.ParsedAlerts AS parsed
                INNER JOIN dbo.Alerts AS alerts ON alerts.Id = parsed.Id
                WHERE parsed.MonitorCondition = N'Resolved'
                  AND parsed.AlertId <> N''
                GROUP BY parsed.AlertId
            )
            UPDATE lifecycle
            SET lifecycle.ResolvedAt = resolutions.ResolvedAt,
                lifecycle.IsCritical = CASE
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM dbo.ParsedAlerts AS critical
                        WHERE critical.AlertId = lifecycle.AlertId
                          AND critical.IsCritical = 1
                    ) THEN 1
                    ELSE lifecycle.IsCritical
                END
            FROM dbo.ParsedAlerts AS lifecycle
            INNER JOIN Resolutions AS resolutions ON resolutions.AlertId = lifecycle.AlertId;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IsCritical", table: "ParsedAlerts");
        migrationBuilder.DropColumn(name: "ResolvedAt", table: "ParsedAlerts");
    }
}
