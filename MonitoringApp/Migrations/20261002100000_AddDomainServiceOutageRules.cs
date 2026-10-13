using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261002100000_AddDomainServiceOutageRules")]
public sealed class AddDomainServiceOutageRules : Migration
{
    private static readonly Guid DomainOutageRuleId = new("c4c346f4-2f67-465e-94b0-c018a6de0001");
    private static readonly Guid SiteOutageRuleId = new("c4c346f4-2f67-465e-94b0-c018a6de0002");
    private static readonly Guid SystemOutageRuleId = new("47a96c56-ccf5-4f4e-97ce-6a72bb462f91");

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            $"UPDATE [AlertRules] SET [Priority] = 30 WHERE [Id] = '{SystemOutageRuleId}';");

        migrationBuilder.InsertData(
            table: "AlertRules",
            columns:
            [
                "Id", "Name", "Enabled", "Priority", "RuleType", "AlertNameContains",
                "QueryResultType", "ConditionType", "Threshold", "FailedItemName", "CategoryName",
                "ApplyToTarget", "Collapsed", "Tone", "InventoryRole", "IsCritical",
                "AnalysisClassification"
            ],
            columnTypes:
            [
                "uniqueidentifier", "nvarchar(256)", "bit", "int", "nvarchar(64)", "nvarchar(256)",
                "nvarchar(128)", "nvarchar(64)", "int", "nvarchar(256)", "nvarchar(256)",
                "bit", "bit", "nvarchar(32)", "nvarchar(256)", "bit", "nvarchar(64)"
            ],
            values: new object[,]
            {
                {
                    DomainOutageRuleId, "Domain controller majority indicates domain service outage",
                    true, 10, AlertRuleTypes.Categorization, "", "",
                    AlertRuleConditionTypes.DomainControllerPercentageInDomain, 50, "",
                    "Domain Service Outage", false, false, "failure", "", false,
                    AlertRuleAnalysisClassifications.None
                },
                {
                    SiteOutageRuleId, "All site domain controllers indicate site outage",
                    true, 20, AlertRuleTypes.Categorization, "", "",
                    AlertRuleConditionTypes.AllDomainControllersInSite, 0, "",
                    "Domain Service Site Outage", false, false, "failure", "", false,
                    AlertRuleAnalysisClassifications.None
                }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DeleteData(
            table: "AlertRules",
            keyColumn: "Id",
            keyValues: [DomainOutageRuleId, SiteOutageRuleId]);
        migrationBuilder.Sql(
            $"UPDATE [AlertRules] SET [Priority] = 10 WHERE [Id] = '{SystemOutageRuleId}';");
    }
}
