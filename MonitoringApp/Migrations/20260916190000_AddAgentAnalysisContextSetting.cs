using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260916190000_AddAgentAnalysisContextSetting")]
public sealed class AddAgentAnalysisContextSetting : Migration
{
    private const string SettingName = "AgentAnalysisContext";
    private const string JsonValue =
        "{\"InventoryCorrelationLayers\":[\"Domain\",\"Role\"]}";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql($"""
            IF NOT EXISTS (SELECT 1 FROM [dbo].[Settings] WHERE [Name] = N'{SettingName}')
            BEGIN
                INSERT INTO [dbo].[Settings] ([Name], [JsonValue])
                VALUES (N'{SettingName}', N'{JsonValue}');
            END;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DeleteData(
            table: "Settings",
            keyColumn: "Name",
            keyValue: SettingName);
}