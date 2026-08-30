using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260829120000_AddComputerInventory")]
public sealed class AddComputerInventory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ComputerInventory",
            columns: table => new
            {
                SubscriptionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Domain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Site = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Computer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ComputerInventory", entry => new { entry.SubscriptionId, entry.Computer });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ComputerInventory");
}