using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831100000_AddSettings")]
public sealed class AddSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Settings",
            columns: table => new
            {
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                JsonValue = table.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Settings", setting => setting.Name));

        migrationBuilder.InsertData(
            table: "Settings",
            columns: ["Name", "JsonValue"],
            values: new object[,]
            {
                { "Authentication", "{\"Type\":\"sql\"}" },
                { "AlertHistory", "{\"Days\":7}" },
                {
                    "AlertGraph",
                    "{\"Layer1\":[{\"Value\":\"Subscription\",\"Label\":\"Subscription\"},{\"Value\":\"ResourceGroup\",\"Label\":\"Resourcegroup\"}],\"Layer2\":[{\"Value\":\"AlertName\",\"Label\":\"AlertName\"},{\"Value\":\"ResourceGroup\",\"Label\":\"Resourcegroup\"},{\"Value\":\"Site\",\"Label\":\"Site\"}],\"Layer3\":[{\"Value\":\"Target\",\"Label\":\"Target\"},{\"Value\":\"Site\",\"Label\":\"Site\"}],\"DefaultLayer1\":\"ResourceGroup\",\"DefaultLayer2\":\"Site\",\"DefaultLayer3\":\"Target\"}"
                }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Settings");
}
