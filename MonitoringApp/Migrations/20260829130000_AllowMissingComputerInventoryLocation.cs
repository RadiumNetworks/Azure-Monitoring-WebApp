using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260829130000_AllowMissingComputerInventoryLocation")]
public sealed class AllowMissingComputerInventoryLocation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Domain",
            table: "ComputerInventory",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256);

        migrationBuilder.AlterColumn<string>(
            name: "Site",
            table: "ComputerInventory",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE [ComputerInventory] SET [Domain] = N'' WHERE [Domain] IS NULL; " +
            "UPDATE [ComputerInventory] SET [Site] = N'' WHERE [Site] IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "Domain",
            table: "ComputerInventory",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Site",
            table: "ComputerInventory",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256,
            oldNullable: true);
    }
}