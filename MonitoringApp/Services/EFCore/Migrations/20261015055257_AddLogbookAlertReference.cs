using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLogbookAlertReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AlertRecordId",
                table: "LogbookEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogbookEntries_AlertRecordId",
                table: "LogbookEntries",
                column: "AlertRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LogbookEntries_AlertRecordId",
                table: "LogbookEntries");

            migrationBuilder.DropColumn(
                name: "AlertRecordId",
                table: "LogbookEntries");
        }
    }
}
