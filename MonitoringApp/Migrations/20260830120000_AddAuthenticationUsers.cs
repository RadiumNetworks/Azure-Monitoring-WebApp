using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260830120000_AddAuthenticationUsers")]
public sealed class AddAuthenticationUsers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "AuthenticationUsers",
            columns: table => new
            {
                Username = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AuthenticationUsers", user => user.Username));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AuthenticationUsers");
}