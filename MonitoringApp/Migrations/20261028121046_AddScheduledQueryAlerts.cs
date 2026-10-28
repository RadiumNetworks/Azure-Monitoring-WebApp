using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledQueryAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledQueryNotifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeliveredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledQueryNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledQueryRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    WorkspaceKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Domain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Site = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Computers = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Kql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    WindowMinutes = table.Column<int>(type: "int", nullable: false),
                    IngestionDelayMinutes = table.Column<int>(type: "int", nullable: false),
                    Comparison = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Threshold = table.Column<double>(type: "float", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CategoryName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NextRunUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastWindowEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledQueryRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledQueryTargetStates",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Computer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Value = table.Column<double>(type: "float", nullable: true),
                    CheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActiveAlertId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FiredUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledQueryTargetStates", x => new { x.RuleId, x.SubscriptionId, x.Computer });
                    table.ForeignKey(
                        name: "FK_ScheduledQueryTargetStates_ScheduledQueryRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "ScheduledQueryRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledQueryNotifications_DeliveredUtc_Id",
                table: "ScheduledQueryNotifications",
                columns: new[] { "DeliveredUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledQueryRules_Enabled_NextRunUtc",
                table: "ScheduledQueryRules",
                columns: new[] { "Enabled", "NextRunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledQueryRules_Name",
                table: "ScheduledQueryRules",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledQueryNotifications");

            migrationBuilder.DropTable(
                name: "ScheduledQueryTargetStates");

            migrationBuilder.DropTable(
                name: "ScheduledQueryRules");
        }
    }
}
