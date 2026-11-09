using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryHeartbeatDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CertificatesJson",
                table: "ComputerInventory",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<double>(
                name: "NtdsFileSizeInGb",
                table: "ComputerInventory",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServicesJson",
                table: "ComputerInventory",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CertificatesJson",
                table: "ComputerInventory");

            migrationBuilder.DropColumn(
                name: "NtdsFileSizeInGb",
                table: "ComputerInventory");

            migrationBuilder.DropColumn(
                name: "ServicesJson",
                table: "ComputerInventory");
        }
    }
}
