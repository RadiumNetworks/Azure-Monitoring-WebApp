using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalUserAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricalUserAliases",
                columns: table => new
                {
                    AliasUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MainUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalUserAliases", x => x.AliasUserId);
                    table.CheckConstraint("CK_HistoricalUserAliases_DistinctUsers", "[AliasUserId] <> [MainUserId]");
                    table.ForeignKey(
                        name: "FK_HistoricalUserAliases_DirectoryUsers_AliasUserId",
                        column: x => x.AliasUserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HistoricalUserAliases_DirectoryUsers_MainUserId",
                        column: x => x.MainUserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalUserAliases_MainUserId",
                table: "HistoricalUserAliases",
                column: "MainUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [HistoricalUserAliases])
                    THROW 51030, 'Separate historical display aliases before removing their schema.', 1;
                """);
            migrationBuilder.DropTable(
                name: "HistoricalUserAliases");
        }
    }
}
