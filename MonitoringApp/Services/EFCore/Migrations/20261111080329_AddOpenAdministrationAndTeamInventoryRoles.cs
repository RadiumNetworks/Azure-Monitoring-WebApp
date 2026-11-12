using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenAdministrationAndTeamInventoryRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ActorUserId",
                table: "IdentityAdministrationRecords",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<bool>(
                name: "IsOpenAuthentication",
                table: "IdentityAdministrationRecords",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "UserTeamInventoryRoles",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTeamInventoryRoles", x => new { x.TeamId, x.RoleKey });
                    table.CheckConstraint("CK_UserTeamInventoryRoles_RoleKey", "LTRIM(RTRIM([RoleKey])) <> ''");
                    table.ForeignKey(
                        name: "FK_UserTeamInventoryRoles_UserTeams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "UserTeams",
                        principalColumn: "Id");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_IdentityAdministrationRecords_Actor",
                table: "IdentityAdministrationRecords",
                sql: "([IsOpenAuthentication] = 1 AND [ActorUserId] IS NULL) OR ([IsOpenAuthentication] = 0 AND [ActorUserId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [IdentityAdministrationRecords] WHERE [IsOpenAuthentication] = 1)
                    OR EXISTS (SELECT 1 FROM [UserTeamInventoryRoles])
                    THROW 51030, 'Cannot downgrade after Open administration or team role assignments. Use a reviewed restore or forward fix.', 1;
                """);
            migrationBuilder.DropTable(
                name: "UserTeamInventoryRoles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IdentityAdministrationRecords_Actor",
                table: "IdentityAdministrationRecords");

            migrationBuilder.DropColumn(
                name: "IsOpenAuthentication",
                table: "IdentityAdministrationRecords");

            migrationBuilder.AlterColumn<Guid>(
                name: "ActorUserId",
                table: "IdentityAdministrationRecords",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
