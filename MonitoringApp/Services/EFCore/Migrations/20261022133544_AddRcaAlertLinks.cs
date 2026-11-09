using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRcaAlertLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RootCauseAnalyses_RuleId",
                table: "RootCauseAnalyses");

            migrationBuilder.CreateTable(
                name: "RcaAlertLinks",
                columns: table => new
                {
                    AlertEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RcaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RcaAlertLinks", x => x.AlertEventId);
                    table.ForeignKey(
                        name: "FK_RcaAlertLinks_Alerts_AlertEventId",
                        column: x => x.AlertEventId,
                        principalTable: "Alerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RcaAlertLinks_RootCauseAnalyses_RcaId",
                        column: x => x.RcaId,
                        principalTable: "RootCauseAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RootCauseAnalyses_RuleId",
                table: "RootCauseAnalyses",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_RcaAlertLinks_RcaId",
                table: "RcaAlertLinks",
                column: "RcaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RcaAlertLinks");

            migrationBuilder.DropIndex(
                name: "IX_RootCauseAnalyses_RuleId",
                table: "RootCauseAnalyses");

            migrationBuilder.CreateIndex(
                name: "IX_RootCauseAnalyses_RuleId",
                table: "RootCauseAnalyses",
                column: "RuleId",
                unique: true,
                filter: "[IsActive] = 1");
        }
    }
}
