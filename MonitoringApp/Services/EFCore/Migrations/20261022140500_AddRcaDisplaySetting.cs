using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261022140500_AddRcaDisplaySetting")]
public sealed class AddRcaDisplaySetting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'RcaDisplay')
            BEGIN
                INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
                VALUES (N'RcaDisplay',
                    N'{"OpenUnassignedColor":"red","ClosedColor":"green","OtherColor":"orange"}');
            END;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DeleteData(
            table: "Settings",
            keyColumn: "Name",
            keyColumnType: "nvarchar(128)",
            keyValue: "RcaDisplay");
}
