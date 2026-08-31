using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831130000_DefaultAuthenticationToSql")]
public sealed class DefaultAuthenticationToSql : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "UPDATE [Settings] SET [JsonValue] = N'{\"Type\":\"sql\"}' " +
            "WHERE [Name] = N'Authentication' AND JSON_VALUE([JsonValue], '$.Type') = N'open';");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "UPDATE [Settings] SET [JsonValue] = N'{\"Type\":\"open\"}' " +
            "WHERE [Name] = N'Authentication' AND JSON_VALUE([JsonValue], '$.Type') = N'sql';");
}