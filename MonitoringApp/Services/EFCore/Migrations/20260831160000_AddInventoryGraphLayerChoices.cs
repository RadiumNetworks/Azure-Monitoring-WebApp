using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20260831160000_AddInventoryGraphLayerChoices")]
public sealed class AddInventoryGraphLayerChoices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE [dbo].[Settings]
                        SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer1', JSON_QUERY(N'{"Value":"Domain","Label":"Domain"}'))
            WHERE [Name] = N'AlertGraph'
              AND ISJSON([JsonValue]) = 1
                            AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer1') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Domain');

                        UPDATE [dbo].[Settings]
                        SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer1', JSON_QUERY(N'{"Value":"Role","Label":"Role"}'))
                        WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
                            AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer1') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Role');

                        UPDATE [dbo].[Settings]
                        SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer2', JSON_QUERY(N'{"Value":"Domain","Label":"Domain"}'))
                        WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
                            AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer2') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Domain');

                        UPDATE [dbo].[Settings]
                        SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer2', JSON_QUERY(N'{"Value":"Role","Label":"Role"}'))
                        WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
                            AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer2') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Role');

                        UPDATE [dbo].[Settings]
                        SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer3', JSON_QUERY(N'{"Value":"Domain","Label":"Domain"}'))
                        WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
                            AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer3') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Domain');

                        UPDATE [dbo].[Settings]
                        SET [JsonValue] = JSON_MODIFY([JsonValue], 'append $.Layer3', JSON_QUERY(N'{"Value":"Role","Label":"Role"}'))
                        WHERE [Name] = N'AlertGraph' AND ISJSON([JsonValue]) = 1
                            AND NOT EXISTS (SELECT 1 FROM OPENJSON([JsonValue], '$.Layer3') WITH ([Value] nvarchar(64) '$.Value') WHERE [Value] = N'Role');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // User-managed graph configuration is intentionally not rewritten on downgrade.
    }
}
