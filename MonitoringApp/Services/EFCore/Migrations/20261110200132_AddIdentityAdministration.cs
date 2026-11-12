using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityAdministrationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetTeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityAdministrationRecords", x => x.Id);
                    table.CheckConstraint("CK_IdentityAdministrationRecords_Target", "([TargetUserId] IS NOT NULL AND [TargetTeamId] IS NULL) OR ([TargetUserId] IS NULL AND [TargetTeamId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_IdentityAdministrationRecords_DirectoryUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IdentityAdministrationRecords_DirectoryUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IdentityAdministrationRecords_UserTeams_TargetTeamId",
                        column: x => x.TargetTeamId,
                        principalTable: "UserTeams",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAdministrationRecords_ActorUserId",
                table: "IdentityAdministrationRecords",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAdministrationRecords_TargetTeamId_OccurredAt",
                table: "IdentityAdministrationRecords",
                columns: new[] { "TargetTeamId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAdministrationRecords_TargetUserId_OccurredAt",
                table: "IdentityAdministrationRecords",
                columns: new[] { "TargetUserId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityAdministrationRecords");
        }
    }
}
