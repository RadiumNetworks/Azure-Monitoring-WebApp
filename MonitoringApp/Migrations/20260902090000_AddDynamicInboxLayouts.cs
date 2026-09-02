using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260902090000_AddDynamicInboxLayouts")]
public sealed class AddDynamicInboxLayouts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InboxLayouts",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                OwnerKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                DocumentVersion = table.Column<int>(type: "int", nullable: false),
                Revision = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_InboxLayouts", value => value.Id));

        migrationBuilder.CreateIndex(
            name: "IX_InboxLayouts_OwnerKey",
            table: "InboxLayouts",
            column: "OwnerKey",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "InboxLayouts");
}