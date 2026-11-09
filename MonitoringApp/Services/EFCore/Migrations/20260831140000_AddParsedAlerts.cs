using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831140000_AddParsedAlerts")]
public sealed class AddParsedAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ParsedAlerts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FiredDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                AlertId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                OriginalAlertId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                Severity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                MonitorCondition = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Dimensions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SearchQuery = table.Column<string>(type: "nvarchar(max)", nullable: false),
                QueryResults = table.Column<string>(type: "nvarchar(max)", nullable: false),
                AlertName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                ResourceGroup = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                InventorySubscriptionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                InventoryComputer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ParsedAlerts", value => value.Id);
                table.ForeignKey(
                    name: "FK_ParsedAlerts_Alerts_Id",
                    column: value => value.Id,
                    principalTable: "Alerts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ParsedAlerts_ComputerInventory_InventorySubscriptionId_InventoryComputer",
                    columns: value => new { value.InventorySubscriptionId, value.InventoryComputer },
                    principalTable: "ComputerInventory",
                    principalColumns: ["SubscriptionId", "Computer"],
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ParsedAlerts_AlertId",
            table: "ParsedAlerts",
            column: "AlertId");

        migrationBuilder.CreateIndex(
            name: "IX_ParsedAlerts_InventorySubscriptionId_InventoryComputer",
            table: "ParsedAlerts",
            columns: ["InventorySubscriptionId", "InventoryComputer"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ParsedAlerts");
}
