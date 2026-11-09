using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260830150000_AddAuthenticationUserRole")]
public sealed class AddAuthenticationUserRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "Role",
            table: "AuthenticationUsers",
            type: "nvarchar(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: SqlAuthenticationRoles.Admin);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "Role", table: "AuthenticationUsers");
}