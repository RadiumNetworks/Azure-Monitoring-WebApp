using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRootCauseAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RootCauseAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FollowUpDueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CriticalEndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EvidenceEventIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Analysis = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssignedTo = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootCauseAnalyses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RcaChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RcaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Field = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    After = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RcaChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RcaChanges_RootCauseAnalyses_RcaId",
                        column: x => x.RcaId,
                        principalTable: "RootCauseAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RcaPrompts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RcaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RawPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RcaPrompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RcaPrompts_RootCauseAnalyses_RcaId",
                        column: x => x.RcaId,
                        principalTable: "RootCauseAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RcaChanges_RcaId_CreatedAt",
                table: "RcaChanges",
                columns: new[] { "RcaId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RcaPrompts_RcaId_Sequence",
                table: "RcaPrompts",
                columns: new[] { "RcaId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootCauseAnalyses_RuleId",
                table: "RootCauseAnalyses",
                column: "RuleId",
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RcaChanges");

            migrationBuilder.DropTable(
                name: "RcaPrompts");

            migrationBuilder.DropTable(
                name: "RootCauseAnalyses");
        }
    }
}
