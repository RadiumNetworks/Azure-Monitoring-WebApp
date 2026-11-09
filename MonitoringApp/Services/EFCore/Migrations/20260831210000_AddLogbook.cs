using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831210000_AddLogbook")]
public sealed class AddLogbook : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LogbookEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                User = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Comment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LogbookEntries", value => value.Id));

        migrationBuilder.CreateIndex(
            name: "IX_LogbookEntries_CreatedAt",
            table: "LogbookEntries",
            column: "CreatedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "LogbookEntries");
}
