using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831120000_AddAlertSeverityDisplaySetting")]
public sealed class AddAlertSeverityDisplaySetting : Migration
{
    private const string SettingName = "AlertSeverityDisplay";
    private const string JsonValue =
        "{\"Severities\":[{\"Severity\":\"Sev0\",\"Color\":\"red\",\"FontStyle\":\"bold\"},{\"Severity\":\"Sev1\",\"Color\":\"red\",\"FontStyle\":\"bold\"},{\"Severity\":\"Sev2\",\"Color\":\"yellow\",\"FontStyle\":\"bold\"},{\"Severity\":\"Sev3\",\"Color\":\"gray\",\"FontStyle\":\"normal\"},{\"Severity\":\"Sev4\",\"Color\":\"green\",\"FontStyle\":\"normal\"}],\"Default\":{\"Color\":\"black\",\"FontStyle\":\"normal\"}}";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.InsertData(
            table: "Settings",
            columns: ["Name", "JsonValue"],
            columnTypes: ["nvarchar(128)", "nvarchar(max)"],
            values: [SettingName, JsonValue]);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DeleteData(
            table: "Settings",
            keyColumn: "Name",
            keyColumnType: "nvarchar(128)",
            keyValue: SettingName);
}