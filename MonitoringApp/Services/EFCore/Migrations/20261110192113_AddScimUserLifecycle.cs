using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddScimUserLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ScimUserStates",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ScimUserStates");
        }
    }
}
