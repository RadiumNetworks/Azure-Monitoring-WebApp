using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAnalysisReviewEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentAnalysisReviewEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentAnalysisJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultVersion = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAnalysisReviewEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentAnalysisReviewEvents_AgentAnalysisJobs_AgentAnalysisJobId",
                        column: x => x.AgentAnalysisJobId,
                        principalTable: "AgentAnalysisJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAnalysisReviewEvents_AgentAnalysisJobId_CreatedAt",
                table: "AgentAnalysisReviewEvents",
                columns: new[] { "AgentAnalysisJobId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentAnalysisReviewEvents");
        }
    }
}
