using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261105080000_AddReplicationInventoryWarningRule")]
public sealed class AddReplicationInventoryWarningRule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            IF NOT EXISTS (SELECT 1 FROM [AlertRules] WHERE [RuleType] = N'ReplicationInventoryWarning')
            BEGIN
                INSERT INTO [AlertRules]
                    ([Id], [Name], [Enabled], [Priority], [RuleType], [AlertNameContains],
                     [QueryResultType], [ConditionType], [Threshold], [FailedItemName], [CategoryName],
                     [ApplyToTarget], [Collapsed], [Tone], [InventoryRole], [IsCritical], [AnalysisClassification])
                VALUES
                    ('e9b56b85-cd67-4cd2-b693-62bf5a6d0001', N'Warn about unknown replication inventory targets', 1, 0,
                     N'ReplicationInventoryWarning', N'', N'Replication', N'', 0, N'', N'',
                     0, 0, N'info', N'', 0, N'None');
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM [AlertRules]
            WHERE [Id] = 'e9b56b85-cd67-4cd2-b693-62bf5a6d0001'
                AND [RuleType] = N'ReplicationInventoryWarning';
            """);
    }
}
