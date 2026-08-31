using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260829110000_ExpandSystemOutageRuleByDefault")]
public sealed class ExpandSystemOutageRuleByDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "UPDATE [AlertRules] SET [Collapsed] = CAST(0 AS bit) " +
            "WHERE [Id] = '47a96c56-ccf5-4f4e-97ce-6a72bb462f91';");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "UPDATE [AlertRules] SET [Collapsed] = CAST(1 AS bit) " +
            "WHERE [Id] = '47a96c56-ccf5-4f4e-97ce-6a72bb462f91';");
}