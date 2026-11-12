using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddScimGroupLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ScimGroups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ScimGroupMembers",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ScimGroups");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ScimGroupMembers");
        }
    }
}
