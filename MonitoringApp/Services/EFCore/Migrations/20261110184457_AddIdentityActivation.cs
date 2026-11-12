using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsesStableIdentity",
                table: "DirectoryUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "IdentityMigrationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VerificationNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    OriginalStateJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMigrationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityMigrationRecords_DirectoryUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IdentityMigrationRecords_DirectoryUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMigrationRecords_ApprovedByUserId",
                table: "IdentityMigrationRecords",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMigrationRecords_UserId_Fingerprint",
                table: "IdentityMigrationRecords",
                columns: new[] { "UserId", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityMigrationRecords");

            migrationBuilder.DropColumn(
                name: "UsesStableIdentity",
                table: "DirectoryUsers");
        }
    }
}
