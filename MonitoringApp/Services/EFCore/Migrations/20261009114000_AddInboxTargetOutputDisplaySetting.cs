using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261009114000_AddInboxTargetOutputDisplaySetting")]
public sealed class AddInboxTargetOutputDisplaySetting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'InboxTargetOutputDisplay')
            BEGIN
                INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
                VALUES (N'InboxTargetOutputDisplay',
                    N'{"CountColors":[{"MinimumCount":1,"Color":"yellow"},{"MinimumCount":2,"Color":"orange"},{"MinimumCount":3,"Color":"red"}],"SuppressedColor":"green","CriticalOutageAlwaysRed":true,"BoldOutageNames":true,"ShowDomain":true}');
            END;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DeleteData(
            table: "Settings",
            keyColumn: "Name",
            keyColumnType: "nvarchar(128)",
            keyValue: "InboxTargetOutputDisplay");
}
