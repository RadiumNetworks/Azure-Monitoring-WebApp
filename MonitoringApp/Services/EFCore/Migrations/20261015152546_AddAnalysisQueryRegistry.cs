using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisQueryRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisQueryDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Kql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VisualizationType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DefaultTimeRangeMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxTimeRangeMinutes = table.Column<int>(type: "int", nullable: false),
                    DefaultBinMinutes = table.Column<int>(type: "int", nullable: false),
                    MinimumRole = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisQueryDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisQueryVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QueryDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisQueryVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalysisQueryVersions_AnalysisQueryDefinitions_QueryDefinitionId",
                        column: x => x.QueryDefinitionId,
                        principalTable: "AnalysisQueryDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisQueryWorkspaces",
                columns: table => new
                {
                    QueryDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastValidationStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastValidationMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisQueryWorkspaces", x => new { x.QueryDefinitionId, x.WorkspaceKey });
                    table.ForeignKey(
                        name: "FK_AnalysisQueryWorkspaces_AnalysisQueryDefinitions_QueryDefinitionId",
                        column: x => x.QueryDefinitionId,
                        principalTable: "AnalysisQueryDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisQueryDefinitions_Key",
                table: "AnalysisQueryDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisQueryDefinitions_Status_Enabled",
                table: "AnalysisQueryDefinitions",
                columns: new[] { "Status", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisQueryVersions_QueryDefinitionId_Version",
                table: "AnalysisQueryVersions",
                columns: new[] { "QueryDefinitionId", "Version" },
                unique: true);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS
                (
                    SELECT 1 FROM [AnalysisQueryDefinitions]
                    WHERE [Id] = 'a1000000-0000-0000-0000-000000000001' OR [Key] = N'cpu-usage'
                )
                INSERT INTO [AnalysisQueryDefinitions]
                    ([Id], [Key], [Name], [Description], [Category], [Kql], [VisualizationType],
                     [Unit], [DefaultTimeRangeMinutes], [MaxTimeRangeMinutes], [DefaultBinMinutes],
                     [MinimumRole], [Status], [Enabled], [Version], [PublishedVersionId], [UpdatedAt], [UpdatedBy])
                VALUES
                    ('a1000000-0000-0000-0000-000000000001', N'cpu-usage', N'CPU usage',
                     N'Average total processor utilization per selected computer.', N'Performance',
                     N'Perf
                | where Computer in~ (_SelectedSystems)
                | where ObjectName == "Processor"
                | where CounterName == "% Processor Time"
                | where InstanceName == "_Total"
                | summarize Value = avg(CounterValue) by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer
                | project Timestamp, Series, Value
                | order by Timestamp asc',
                     N'LineChart', N'Percent', 1440, 10080, 5, N'Operator', N'Draft', 1, 0, NULL, SYSUTCDATETIME(), N'System');

                IF NOT EXISTS
                (
                    SELECT 1 FROM [AnalysisQueryDefinitions]
                    WHERE [Id] = 'a1000000-0000-0000-0000-000000000002' OR [Key] = N'ram-usage'
                )
                INSERT INTO [AnalysisQueryDefinitions]
                    ([Id], [Key], [Name], [Description], [Category], [Kql], [VisualizationType],
                     [Unit], [DefaultTimeRangeMinutes], [MaxTimeRangeMinutes], [DefaultBinMinutes],
                     [MinimumRole], [Status], [Enabled], [Version], [PublishedVersionId], [UpdatedAt], [UpdatedBy])
                VALUES
                    ('a1000000-0000-0000-0000-000000000002', N'ram-usage', N'RAM usage',
                     N'Average committed memory utilization per selected computer.', N'Performance',
                     N'Perf
                | where Computer in~ (_SelectedSystems)
                | where ObjectName == "Memory"
                | where CounterName == "% Committed Bytes In Use"
                | summarize Value = avg(CounterValue) by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer
                | project Timestamp, Series, Value
                | order by Timestamp asc',
                     N'LineChart', N'Percent', 1440, 10080, 5, N'Operator', N'Draft', 1, 0, NULL, SYSUTCDATETIME(), N'System');

                IF NOT EXISTS
                (
                    SELECT 1 FROM [AnalysisQueryDefinitions]
                    WHERE [Id] = 'a1000000-0000-0000-0000-000000000003' OR [Key] = N'kerberos-authentications'
                )
                INSERT INTO [AnalysisQueryDefinitions]
                    ([Id], [Key], [Name], [Description], [Category], [Kql], [VisualizationType],
                     [Unit], [DefaultTimeRangeMinutes], [MaxTimeRangeMinutes], [DefaultBinMinutes],
                     [MinimumRole], [Status], [Enabled], [Version], [PublishedVersionId], [UpdatedAt], [UpdatedBy])
                VALUES
                    ('a1000000-0000-0000-0000-000000000003', N'kerberos-authentications', N'Kerberos authentications',
                     N'Counts Kerberos ticket-granting and service-ticket events 4768 and 4769.', N'Security',
                     N'SecurityEvent
                | where Computer in~ (_SelectedSystems)
                | where EventID in (4768, 4769)
                | summarize Value = count() by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer, Dimension = tostring(EventID)
                | project Timestamp, Series, Value, Dimension
                | order by Timestamp asc',
                     N'LineChart', N'Count', 240, 1440, 5, N'Operator', N'Draft', 1, 0, NULL, SYSUTCDATETIME(), N'System');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisQueryVersions");

            migrationBuilder.DropTable(
                name: "AnalysisQueryWorkspaces");

            migrationBuilder.DropTable(
                name: "AnalysisQueryDefinitions");
        }
    }
}
