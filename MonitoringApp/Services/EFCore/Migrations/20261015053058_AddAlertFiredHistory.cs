using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

public partial class AddAlertFiredHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AlertHistory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                AlertId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                Severity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SignalType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                MonitorCondition = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Target = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                ResourceGroup = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                SubscriptionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                FiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                SearchResultsUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                RawJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AlertHistory", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AlertHistory_AlertId",
            table: "AlertHistory",
            column: "AlertId");

        migrationBuilder.CreateIndex(
            name: "IX_AlertHistory_ReceivedAt",
            table: "AlertHistory",
            column: "ReceivedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AlertHistory");
}
