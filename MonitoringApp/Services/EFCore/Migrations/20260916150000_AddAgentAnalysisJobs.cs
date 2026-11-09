using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260916150000_AddAgentAnalysisJobs")]
public sealed class AddAgentAnalysisJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentAnalysisJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AnalysisId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ResultVersion = table.Column<int>(type: "int", nullable: false),
                Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                StatusReason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                Revision = table.Column<int>(type: "int", nullable: false),
                Mode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ExecutionProfile = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                PromptVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SnapshotVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ResultContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ScopeType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ScopeValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                PrimaryEventIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SnapshotGeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                DataFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                DataTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AgentAnalysisJobs", row => row.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AgentAnalysisJobs_AnalysisId",
            table: "AgentAnalysisJobs",
            column: "AnalysisId");

        migrationBuilder.CreateIndex(
            name: "IX_AgentAnalysisJobs_CreatedBy_CreatedAt",
            table: "AgentAnalysisJobs",
            columns: new[] { "CreatedBy", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentAnalysisJobs_Status_CreatedAt",
            table: "AgentAnalysisJobs",
            columns: new[] { "Status", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AgentAnalysisJobs");
}