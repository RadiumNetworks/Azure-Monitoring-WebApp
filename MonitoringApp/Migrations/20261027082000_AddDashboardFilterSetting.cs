using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261027082000_AddDashboardFilterSetting")]
public sealed class AddDashboardFilterSetting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'DashboardFilters')
            BEGIN
                INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
                VALUES (N'DashboardFilters',
                    N'{"Workspace":null,"Domain":null,"Site":null,"Role":null}');
            END;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DeleteData(
            table: "Settings",
            keyColumn: "Name",
            keyColumnType: "nvarchar(128)",
            keyValue: "DashboardFilters");
}
